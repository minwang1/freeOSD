using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Diagnostics;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Core.Services;

namespace MonitorControl.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayColorProfileService : IDisplayColorProfileService
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ScopeSystemWide = 0;
    private const int ScopeCurrentUser = 1;
    private const int ProfileTypeIcc = 0;
    private const int ProfileSubtypeNone = 0;
    private const int CapabilityVideoCardGammaTable = 1;
    private const int CapabilityMicrosoftHardwareColorV2 = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ushort[]> _rgbPreviewOriginalRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _backupPath;

    public WindowsDisplayColorProfileService(string? backupPath = null) => _backupPath = backupPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorControl", "color-profile-backups.json");

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public Task<IReadOnlyList<DisplayColorProfile>> GetProfilesAsync(string displayName, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DisplayColorProfile>>(() =>
        {
            EnsureSupported();
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetTarget(displayName);
            var scope = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var currentScope);
            EnsureHResult(scope, "Could not determine the active color-profile scope");
            var listResult = Native.ColorProfileGetDisplayList(currentScope, target.AdapterId, target.SourceId, out var profileNames, out var count);
            EnsureHResult(listResult, "Could not enumerate display color profiles");
            try
            {
                var active = GetActivePath(target, currentScope);
                var profiles = new List<DisplayColorProfile>();
                for (var index = 0; index < count; index++)
                {
                    var namePointer = Marshal.ReadIntPtr(profileNames, index * IntPtr.Size);
                    var path = Marshal.PtrToStringUni(namePointer);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    path = NormalizeInstalledProfilePath(path);
                    profiles.Add(new DisplayColorProfile(Path.GetFileNameWithoutExtension(path), path, PathsEqual(path, active)));
                }
                if (!profiles.Any(profile => profile.IsDefault) && !string.IsNullOrWhiteSpace(active))
                    profiles.Insert(0, new DisplayColorProfile(Path.GetFileNameWithoutExtension(active), active, true));
                Trace.TraceInformation($"display.icc.profiles.enumerated display={displayName} scope={currentScope} count={count} active={active ?? "<none>"} paths={string.Join("|", profiles.Select(profile => profile.Path))}");
                return profiles.OrderByDescending(profile => profile.IsDefault).ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            finally { if (profileNames != IntPtr.Zero) Native.LocalFree(profileNames); }
        }, cancellationToken);

    public Task<string?> GetActiveProfilePathAsync(string displayName, CancellationToken cancellationToken = default) =>
        Task.Run<string?>(() =>
        {
            EnsureSupported(); cancellationToken.ThrowIfCancellationRequested();
            var target = GetTarget(displayName);
            var result = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
            EnsureHResult(result, "Could not determine the active color-profile scope");
            return GetActivePath(target, scope);
        }, cancellationToken);

    public Task<DisplayColorPipelineCapabilities> GetPipelineCapabilitiesAsync(string displayName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            EnsureSupported();
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetTarget(displayName);
            var scopeResult = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
            EnsureHResult(scopeResult, "Could not determine the color-profile scope for capability detection");

            var vcgt = new Native.DeviceVcgtCapabilities { Size = (uint)Marshal.SizeOf<Native.DeviceVcgtCapabilities>() };
            var vcgtResult = Native.ColorProfileGetDeviceCapabilities(scope, target.AdapterId, target.SourceId, CapabilityVideoCardGammaTable, ref vcgt);
            var mhc2 = new Native.DeviceMhc2Capabilities { Size = (uint)Marshal.SizeOf<Native.DeviceMhc2Capabilities>() };
            var mhc2Result = Native.ColorProfileGetDeviceCapabilities(scope, target.AdapterId, target.SourceId, CapabilityMicrosoftHardwareColorV2, ref mhc2);
            var calibrationStateRead = Native.WcsGetCalibrationManagementState(out var calibrationManagementEnabled);
            var calibrationManagementError = calibrationStateRead ? 0 : Marshal.GetLastWin32Error();

            var capabilities = new DisplayColorPipelineCapabilities(
                scope,
                vcgtResult >= 0,
                vcgtResult >= 0 && vcgt.SupportsVcgt != 0,
                vcgtResult,
                mhc2Result >= 0,
                mhc2Result >= 0 && mhc2.SupportsMhc2 != 0,
                mhc2.RegammaLutEntryCount,
                mhc2.CscXyzMatrixRows,
                mhc2.CscXyzMatrixColumns,
                mhc2Result,
                calibrationStateRead,
                calibrationStateRead && calibrationManagementEnabled,
                calibrationManagementError);
            Trace.TraceInformation($"display.icc.pipeline.capabilities display={displayName} scope={scope} adapter={target.AdapterId.HighPart:X8}:{target.AdapterId.LowPart:X8} source={target.SourceId} vcgtHResult=0x{vcgtResult:X8} supportsVcgt={capabilities.SupportsVcgt} mhc2HResult=0x{mhc2Result:X8} supportsMhc2={capabilities.SupportsMhc2} lutEntries={mhc2.RegammaLutEntryCount} csc={mhc2.CscXyzMatrixRows}x{mhc2.CscXyzMatrixColumns} calibrationStateRead={calibrationStateRead} calibrationManagementEnabled={capabilities.IsCalibrationManagementEnabled} calibrationWin32Error={calibrationManagementError}");
            return capabilities;
        }, cancellationToken);

    public async Task<string> ImportProfileAsync(string displayName, string sourcePath, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("ICC/ICM profile file was not found.", sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".icc", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".icm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a file with an .icc or .icm extension.");

        // Do not derive the COLOR directory from the Windows System directory: Windows
        // owns this location and exposes it through GetColorDirectoryW.
        var systemColorDirectory = GetColorDirectory();
        var installedPath = Path.Combine(systemColorDirectory, Path.GetFileName(sourcePath));
        if (!PathsEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(installedPath)))
        {
            if (!Native.InstallColorProfile(IntPtr.Zero, Path.GetFullPath(sourcePath)))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 5) throw new UnauthorizedAccessException("Installing an ICC profile requires administrator permission. Run Monitor Control as administrator, then retry.");
                throw new InvalidOperationException($"Windows could not install the ICC profile (Win32 error {error}).");
            }
        }
        if (!File.Exists(installedPath))
            throw new FileNotFoundException($"Windows reported the profile as installed, but the expected installed file was not found: {installedPath}", installedPath);

        var target = GetTarget(displayName);
        var result = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
        EnsureHResult(result, "Could not determine the active color-profile scope");
        var profiles = await GetProfilesAsync(displayName, cancellationToken).ConfigureAwait(false);
        Trace.TraceInformation($"display.icc.import.association.started display={displayName} scope={scope} adapter={target.AdapterId.HighPart:X8}:{target.AdapterId.LowPart:X8} source={target.SourceId} profile={installedPath} existing={string.Join("|", profiles.Select(profile => profile.Path))}");
        if (!profiles.Any(profile => PathsEqual(profile.Path, installedPath)))
        {
            result = Native.ColorProfileAddDisplayAssociation(scope, installedPath, target.AdapterId, target.SourceId, false, false);
            Trace.TraceInformation($"display.icc.import.association.returned display={displayName} hresult=0x{result:X8} profile={installedPath}");
            EnsureHResult(result, "Windows installed the profile but could not associate it with this display");
        }
        var associatedProfiles = await GetProfilesAsync(displayName, cancellationToken).ConfigureAwait(false);
        Trace.TraceInformation($"display.icc.import.association.verified display={displayName} scope={scope} profile={installedPath} found={associatedProfiles.Any(profile => PathsEqual(profile.Path, installedPath))} paths={string.Join("|", associatedProfiles.Select(profile => profile.Path))}");
        if (!associatedProfiles.Any(profile => PathsEqual(profile.Path, installedPath)))
            throw new InvalidOperationException($"Windows returned success, but the profile is still not listed for display '{displayName}'. Installed path: '{installedPath}'.");
        return installedPath;
    }

    public async Task<string> GetDefaultRgbProfileFileNameAsync(string displayName, RgbGainAdjustment gains, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        gains.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = GetTarget(displayName);
            var scopeResult = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
            EnsureHResult(scopeResult, "Could not determine the active color-profile scope");
            var activePath = GetActivePath(target, scope)
                ?? throw new InvalidOperationException("The display has no active ICC profile to use as an RGB-adjustment base.");
            var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
            var sourcePath = backups.TryGetValue(displayName, out var backup) ? backup.OriginalProfilePath : activePath;
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("The original ICC profile used as the RGB-adjustment base is unavailable.", sourcePath);
            var contentHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false))).ToLowerInvariant();
            return $"MonitorControl-RGB-{contentHash[..16]}-{gains.RedPercent}-{gains.GreenPercent}-{gains.BluePercent}.icm";
        }
        finally { _gate.Release(); }
    }

    public Task PreviewRgbAdjustmentAsync(string displayName, RgbGainAdjustment gains, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            EnsureSupported();
            gains.Validate();
            _gate.Wait(cancellationToken);
            try
            {
                if (gains == RgbGainAdjustment.Neutral)
                {
                    RestoreRgbPreviewCore(displayName);
                    return;
                }

                var displayDc = Native.CreateDisplayDc("DISPLAY", displayName, null, IntPtr.Zero);
                if (displayDc == IntPtr.Zero)
                    throw new InvalidOperationException($"Windows could not open display '{displayName}' for temporary gamma preview (Win32 error {Marshal.GetLastWin32Error()}).");
                try
                {
                    if (!_rgbPreviewOriginalRamps.TryGetValue(displayName, out var originalRamp))
                    {
                        originalRamp = new ushort[3 * 256];
                        if (!Native.GetDeviceGammaRamp(displayDc, originalRamp))
                            throw new PlatformNotSupportedException("The display driver does not expose a readable gamma ramp; temporary RGB preview is unavailable.");
                        _rgbPreviewOriginalRamps[displayName] = originalRamp;
                    }

                    var previewRamp = new ushort[3 * 256];
                    var gainPercent = new[] { gains.RedPercent, gains.GreenPercent, gains.BluePercent };
                    for (var channel = 0; channel < 3; channel++)
                    {
                        for (var sample = 0; sample < 256; sample++)
                        {
                            var index = channel * 256 + sample;
                            previewRamp[index] = checked((ushort)Math.Clamp(
                                Math.Round(originalRamp[index] * gainPercent[channel] / 100.0, MidpointRounding.AwayFromZero),
                                0,
                                ushort.MaxValue));
                        }
                    }

                    if (!Native.SetDeviceGammaRamp(displayDc, previewRamp))
                    {
                        RestoreRgbPreviewCore(displayName, displayDc);
                        throw new PlatformNotSupportedException($"The display driver rejected temporary gamma preview (Win32 error {Marshal.GetLastWin32Error()}).");
                    }
                    var actualRamp = new ushort[3 * 256];
                    if (!Native.GetDeviceGammaRamp(displayDc, actualRamp) || !actualRamp.SequenceEqual(previewRamp))
                    {
                        RestoreRgbPreviewCore(displayName, displayDc);
                        throw new InvalidOperationException("Windows reported the gamma preview request, but readback did not match. The driver may have silently ignored it.");
                    }
                    Trace.TraceInformation($"display.icc.rgb.preview.applied display={displayName} red={gains.RedPercent} green={gains.GreenPercent} blue={gains.BluePercent} readback=true");
                }
                finally { Native.DeleteDeviceDc(displayDc); }
            }
            finally { _gate.Release(); }
        }, cancellationToken);

    public Task RestoreRgbPreviewAsync(string displayName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            EnsureSupported();
            _gate.Wait(cancellationToken);
            try { RestoreRgbPreviewCore(displayName); }
            finally { _gate.Release(); }
        }, cancellationToken);

    private void RestoreRgbPreviewCore(string displayName, IntPtr existingDisplayDc = default)
    {
        if (!_rgbPreviewOriginalRamps.TryGetValue(displayName, out var originalRamp)) return;
        var displayDc = existingDisplayDc;
        var ownsDisplayDc = displayDc == IntPtr.Zero;
        if (ownsDisplayDc) displayDc = Native.CreateDisplayDc("DISPLAY", displayName, null, IntPtr.Zero);
        if (displayDc == IntPtr.Zero)
            throw new InvalidOperationException($"Windows could not open display '{displayName}' to restore its original gamma ramp.");
        try
        {
            if (!Native.SetDeviceGammaRamp(displayDc, originalRamp))
                throw new InvalidOperationException($"Windows could not restore the original gamma ramp for '{displayName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            var actualRamp = new ushort[3 * 256];
            if (!Native.GetDeviceGammaRamp(displayDc, actualRamp) || !actualRamp.SequenceEqual(originalRamp))
                throw new InvalidOperationException($"Gamma ramp restoration for '{displayName}' could not be verified.");
            _rgbPreviewOriginalRamps.Remove(displayName);
            Trace.TraceInformation($"display.icc.rgb.preview.restored display={displayName} readback=true");
        }
        finally
        {
            if (ownsDisplayDc) Native.DeleteDeviceDc(displayDc);
        }
    }

    public async Task<string> ApplyRgbAdjustmentAsync(string displayName, RgbGainAdjustment gains, string? profileFileName = null, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        gains.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetTarget(displayName);
            var scopeResult = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
            EnsureHResult(scopeResult, "Could not determine the active color-profile scope");
            var previousActivePath = GetActivePath(target, scope)
                ?? throw new InvalidOperationException("The display has no active ICC profile to use as a safe RGB-adjustment base.");
            var availableBefore = await GetProfilesAsync(displayName, cancellationToken).ConfigureAwait(false);
            var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
            var hadBackup = backups.TryGetValue(displayName, out var savedBackup);
            var sourceProfilePath = hadBackup ? savedBackup!.OriginalProfilePath : previousActivePath;
            if (!File.Exists(sourceProfilePath))
                throw new FileNotFoundException("The original ICC profile used as the RGB-adjustment base is no longer available.", sourceProfilePath);

            var contentHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceProfilePath, cancellationToken).ConfigureAwait(false))).ToLowerInvariant();
            var generatedName = NormalizeGeneratedRgbProfileFileName(profileFileName ?? $"MonitorControl-RGB-{contentHash[..16]}-{gains.RedPercent}-{gains.GreenPercent}-{gains.BluePercent}.icm");
            var generatedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorControl", "GeneratedProfiles");
            var generatedPath = Path.Combine(generatedDirectory, generatedName);
            IccRgbGainProfileWriter.Create(sourceProfilePath, generatedPath, gains);
            var installedPath = Path.Combine(GetColorDirectory(), generatedName);
            if (File.Exists(installedPath))
            {
                var generatedHash = SHA256.HashData(await File.ReadAllBytesAsync(generatedPath, cancellationToken).ConfigureAwait(false));
                var installedHash = SHA256.HashData(await File.ReadAllBytesAsync(installedPath, cancellationToken).ConfigureAwait(false));
                if (!CryptographicOperations.FixedTimeEquals(generatedHash, installedHash))
                    throw new IOException($"A different color profile already uses the file name '{generatedName}'. Choose another name.");
            }
            else if (!Native.InstallColorProfile(IntPtr.Zero, generatedPath))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 5) throw new UnauthorizedAccessException("Installing the generated RGB profile requires administrator permission.");
                throw new InvalidOperationException($"Windows could not install the generated RGB profile (Win32 error {error}).");
            }
            if (!File.Exists(installedPath))
                throw new FileNotFoundException("Windows did not create the installed RGB profile file.", installedPath);

            var available = availableBefore;
            var isAssociated = available.Any(profile => PathsEqual(profile.Path, installedPath));
            if (!isAssociated)
            {
                var associationResult = Native.ColorProfileAddDisplayAssociation(scope, installedPath, target.AdapterId, target.SourceId, false, false);
                EnsureHResult(associationResult, "Windows installed the generated RGB profile but could not associate it with this display");
                var verifiedProfiles = await GetProfilesAsync(displayName, cancellationToken).ConfigureAwait(false);
                if (!verifiedProfiles.Any(profile => PathsEqual(profile.Path, installedPath)))
                    throw new InvalidOperationException("Windows returned success, but the generated RGB profile is not listed for the display.");
            }

            var addedBackup = false;
            var defaultChangeAttempted = false;
            try
            {
                if (!hadBackup)
                {
                    backups[displayName] = new Backup(scope, previousActivePath, availableBefore.Select(profile => profile.Path).ToArray());
                    await SaveBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
                    addedBackup = true;
                }

                defaultChangeAttempted = true;
                var applyResult = Native.ColorProfileSetDisplayDefaultAssociation(scope, installedPath, ProfileTypeIcc, ProfileSubtypeNone, target.AdapterId, target.SourceId);
                Trace.TraceInformation($"display.icc.rgb.apply.default-returned display={displayName} hresult=0x{applyResult:X8} profile={installedPath}");
                EnsureHResult(applyResult, "Windows could not set the generated RGB profile as the display default");
                var actual = GetActivePath(target, scope);
                if (!PathsEqual(actual, installedPath))
                    throw new InvalidOperationException($"Windows returned a different active profile after RGB apply. Expected '{installedPath}', actual '{actual}'.");

                Trace.TraceInformation($"display.icc.rgb.apply.completed display={displayName} scope={scope} base={sourceProfilePath} active={actual} red={gains.RedPercent} green={gains.GreenPercent} blue={gains.BluePercent}");
                return installedPath;
            }
            catch (Exception applyException)
            {
                if (!defaultChangeAttempted) throw;
                try
                {
                    var rollbackResult = Native.ColorProfileSetDisplayDefaultAssociation(scope, previousActivePath, ProfileTypeIcc, ProfileSubtypeNone, target.AdapterId, target.SourceId);
                    EnsureHResult(rollbackResult, "Windows could not roll back the active profile after RGB apply failed");
                    var rolledBack = GetActivePath(target, scope);
                    if (!PathsEqual(rolledBack, previousActivePath))
                        throw new InvalidOperationException($"RGB apply failed and rollback readback did not match. Expected '{previousActivePath}', actual '{rolledBack}'.");
                    if (addedBackup)
                    {
                        backups.Remove(displayName);
                        await SaveBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
                    }
                    Trace.TraceWarning($"display.icc.rgb.apply.rolled_back display={displayName} previous={previousActivePath} error={applyException.Message}");
                    throw;
                }
                catch (Exception rollbackException) when (!ReferenceEquals(rollbackException, applyException))
                {
                    throw new AggregateException("RGB profile apply failed and restoring the previous active profile also failed. Use Restore Original Profile to retry recovery.", applyException, rollbackException);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyProfileAsync(string displayName, string profilePath, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = GetTarget(displayName);
            var result = Native.ColorProfileGetDisplayUserScope(target.AdapterId, target.SourceId, out var scope);
            EnsureHResult(result, "Could not determine the active color-profile scope");
            var available = GetProfilesAsync(displayName, cancellationToken).GetAwaiter().GetResult();
            if (!available.Any(profile => PathsEqual(profile.Path, profilePath)))
                throw new InvalidOperationException("The selected profile is not associated with this display.");

            var previousActivePath = GetActivePath(target, scope)
                ?? throw new InvalidOperationException("Windows did not return the current display profile; refusing to apply without a recoverable previous profile.");

            var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
            var hadBackup = backups.ContainsKey(displayName);
            if (!hadBackup)
            {
                backups[displayName] = new Backup(scope, previousActivePath, available.Select(profile => profile.Path).ToArray());
                await SaveBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
            }

            var defaultChangeAttempted = false;
            try
            {
                Trace.TraceInformation($"display.icc.apply.default.started display={displayName} scope={scope} profile={profilePath} previous={previousActivePath}");
                defaultChangeAttempted = true;
                result = Native.ColorProfileSetDisplayDefaultAssociation(scope, profilePath, ProfileTypeIcc, ProfileSubtypeNone, target.AdapterId, target.SourceId);
                Trace.TraceInformation($"display.icc.apply.default.returned display={displayName} hresult=0x{result:X8} profile={profilePath}");
                EnsureHResult(result, "Windows could not set the selected display profile as the default");
                var actual = GetActivePath(target, scope);
                if (!PathsEqual(actual, profilePath))
                    throw new InvalidOperationException($"Windows returned a different active profile after applying. Expected '{profilePath}', actual '{actual}'.");
                Trace.TraceInformation($"display.icc.apply.completed display={displayName} active={actual}");
            }
            catch (Exception applyException)
            {
                if (!defaultChangeAttempted) throw;
                try
                {
                    var rollbackResult = Native.ColorProfileSetDisplayDefaultAssociation(scope, previousActivePath, ProfileTypeIcc, ProfileSubtypeNone, target.AdapterId, target.SourceId);
                    EnsureHResult(rollbackResult, "Windows could not restore the previous default profile after apply failed");
                    var restored = GetActivePath(target, scope);
                    if (!PathsEqual(restored, previousActivePath))
                        throw new InvalidOperationException($"Profile apply failed and rollback readback did not match. Expected '{previousActivePath}', actual '{restored}'.");
                    if (!hadBackup)
                    {
                        backups.Remove(displayName);
                        await SaveBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
                    }
                    Trace.TraceWarning($"display.icc.apply.rolled_back display={displayName} previous={previousActivePath} error={applyException.Message}");
                    throw;
                }
                catch (Exception rollbackException) when (!ReferenceEquals(rollbackException, applyException))
                {
                    throw new AggregateException("Applying the selected profile failed and restoring the previous profile also failed. Use Restore Original Profile to recover.", applyException, rollbackException);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreOriginalProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
            if (!backups.TryGetValue(displayName, out var backup)) throw new InvalidOperationException("There is no saved original profile for this display.");
            var target = GetTarget(displayName);
            var associatedProfiles = GetProfilesAsync(displayName, cancellationToken).GetAwaiter().GetResult();
            if (!associatedProfiles.Any(profile => PathsEqual(profile.Path, backup.OriginalProfilePath)))
            {
                var associateResult = Native.ColorProfileAddDisplayAssociation(backup.Scope, backup.OriginalProfilePath, target.AdapterId, target.SourceId, false, false);
                EnsureHResult(associateResult, "Windows could not associate the original display profile for restoration");
            }
            var result = Native.ColorProfileSetDisplayDefaultAssociation(backup.Scope, backup.OriginalProfilePath, ProfileTypeIcc, ProfileSubtypeNone, target.AdapterId, target.SourceId);
            EnsureHResult(result, "Windows could not set the original display profile as the default");
            foreach (var profile in GetProfilesAsync(displayName, cancellationToken).GetAwaiter().GetResult())
            {
                if (!backup.OriginalAssociatedProfiles.Any(path => PathsEqual(path, profile.Path)))
                    Native.ColorProfileRemoveDisplayAssociation(backup.Scope, profile.Path, target.AdapterId, target.SourceId, false);
            }
            var actual = GetActivePath(target, backup.Scope);
            if (!PathsEqual(actual, backup.OriginalProfilePath))
                throw new InvalidOperationException($"Original profile restoration could not be verified. Expected '{backup.OriginalProfilePath}', actual '{actual}'.");
            backups.Remove(displayName);
            await SaveBackupsAsync(backups, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasOriginalProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
        return backups.ContainsKey(displayName);
    }

    public async Task DeleteGeneratedRgbProfileAsync(string displayName, string profilePath, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullPath = Path.GetFullPath(profilePath);
            var colorDirectory = Path.GetFullPath(GetColorDirectory());
            if (!PathsEqual(Path.GetDirectoryName(fullPath), colorDirectory) ||
                !Path.GetFileName(fullPath).StartsWith("MonitorControl-RGB-", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
                throw new InvalidOperationException("Only an installed Monitor Control RGB profile can be deleted here.");

            var inspection = IccProfileInspector.Inspect(fullPath);
            var gains = inspection.RgbGainAdjustment
                ?? throw new InvalidOperationException("The selected file does not contain Monitor Control RGB metadata.");
            gains.Validate();

            var targets = GetActiveTargets();
            if (!targets.Any(target => string.Equals(target.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Could not find active display '{displayName}'.");
            if (!(await GetProfilesAsync(displayName, cancellationToken).ConfigureAwait(false)).Any(profile => PathsEqual(profile.Path, fullPath)))
                throw new InvalidOperationException("The selected profile is not associated with this display.");
            var backups = await LoadBackupsAsync(cancellationToken).ConfigureAwait(false);
            var protectedPaths = backups.Values
                .SelectMany(backup => new[] { backup.OriginalProfilePath }.Concat(backup.OriginalAssociatedProfiles))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (protectedPaths.Contains(fullPath))
                throw new InvalidOperationException("This profile is saved as an original or original-associated profile and cannot be deleted.");

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var display = new TargetDisplay(target.AdapterId, target.SourceId);
                var scopeResult = Native.ColorProfileGetDisplayUserScope(display.AdapterId, display.SourceId, out var scope);
                EnsureHResult(scopeResult, "Could not determine an active display's profile scope");
                if (PathsEqual(GetActivePath(display, scope), fullPath))
                    throw new InvalidOperationException("The selected profile is currently active on a display and cannot be deleted.");
            }

            foreach (var target in targets)
            {
                var display = new TargetDisplay(target.AdapterId, target.SourceId);
                var scopeResult = Native.ColorProfileGetDisplayUserScope(display.AdapterId, display.SourceId, out var scope);
                EnsureHResult(scopeResult, "Could not determine an active display's profile scope");
                if (!GetAssociatedProfilePaths(display, scope).Any(path => PathsEqual(path, fullPath))) continue;
                var result = Native.ColorProfileRemoveDisplayAssociation(scope, fullPath, display.AdapterId, display.SourceId, false);
                EnsureHResult(result, "Windows could not remove the selected profile association");
                if (GetAssociatedProfilePaths(display, scope).Any(path => PathsEqual(path, fullPath)))
                    throw new InvalidOperationException("Windows still lists the profile as associated after removal.");
            }

            if (!Native.UninstallColorProfile(IntPtr.Zero, Path.GetFileName(fullPath), true))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Windows could not uninstall the selected profile (Win32 error {error}).");
            }
            if (File.Exists(fullPath)) throw new IOException("Windows reported the profile removed, but its file remains.");

            var localCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorControl", "GeneratedProfiles", Path.GetFileName(fullPath));
            if (File.Exists(localCopy)) File.Delete(localCopy);
            Trace.TraceInformation($"display.icc.rgb.profile.deleted display={displayName} profile={fullPath} red={gains.RedPercent} green={gains.GreenPercent} blue={gains.BluePercent}");
        }
        finally { _gate.Release(); }
    }

    private static string? GetActivePath(TargetDisplay target, int scope)
    {
        var result = Native.ColorProfileGetDisplayDefault(scope, target.AdapterId, target.SourceId, ProfileTypeIcc, ProfileSubtypeNone, out var pathPointer);
        // A display can have an association list but no default ICC profile. WCS reports
        // that state as HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND); treat it as no selection
        // so profile enumeration/import can proceed. Other HRESULTs still indicate failure.
        if (result == HResultFileNotFound)
        {
            if (pathPointer != IntPtr.Zero) Native.LocalFree(pathPointer);
            return null;
        }
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        try
        {
            var path = pathPointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pathPointer);
            return string.IsNullOrWhiteSpace(path) ? null : NormalizeInstalledProfilePath(path);
        }
        finally { if (pathPointer != IntPtr.Zero) Native.LocalFree(pathPointer); }
    }

    private static string NormalizeInstalledProfilePath(string path)
    {
        // ColorProfileGetDisplayList/GetDisplayDefault may return only the installed
        // profile's filename (for example, "sRGB Color Space Profile.icm"). Resolve
        // those names against Windows' COLOR directory before comparing or displaying.
        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(GetColorDirectory(), path);
        return Path.GetFullPath(fullPath);
    }

    private const int HResultFileNotFound = unchecked((int)0x80070002);

    private static string GetColorDirectory()
    {
        uint bufferSizeBytes = 512;
        while (bufferSizeBytes <= 32768)
        {
            var buffer = new System.Text.StringBuilder(checked((int)((bufferSizeBytes + 1) / 2)));
            if (Native.GetColorDirectory(IntPtr.Zero, buffer, ref bufferSizeBytes))
                return buffer.ToString().TrimEnd('\\');

            var error = Marshal.GetLastWin32Error();
            if (error != 122) // ERROR_INSUFFICIENT_BUFFER
                throw new InvalidOperationException($"Windows could not locate its color-profile directory (Win32 error {error}).");
        }

        throw new InvalidOperationException("Windows returned an unexpectedly large color-profile directory path.");
    }

    private static string NormalizeGeneratedRgbProfileFileName(string fileName)
    {
        var normalized = fileName.Trim();
        if (normalized.Length == 0 || Path.GetFileName(normalized) != normalized || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid file name without a folder path.", nameof(fileName));
        if (!normalized.StartsWith("MonitorControl-RGB-", StringComparison.OrdinalIgnoreCase))
            normalized = "MonitorControl-RGB-" + normalized;
        if (string.IsNullOrEmpty(Path.GetExtension(normalized))) normalized += ".icm";
        var extension = Path.GetExtension(normalized);
        if (!extension.Equals(".icm", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".icc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("RGB profile file names must use the .icm or .icc extension.", nameof(fileName));
        return normalized;
    }

    private static TargetDisplay GetTarget(string displayName)
    {
        var target = GetActiveTargets().FirstOrDefault(item => string.Equals(item.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        if (target is not null) return new TargetDisplay(target.AdapterId, target.SourceId);
        throw new InvalidOperationException($"Could not find an active Windows display path for '{displayName}'.");
    }

    private static IReadOnlyList<string> GetAssociatedProfilePaths(TargetDisplay target, int scope)
    {
        var result = Native.ColorProfileGetDisplayList(scope, target.AdapterId, target.SourceId, out var names, out var count);
        EnsureHResult(result, "Could not enumerate display color profiles during cleanup");
        try
        {
            var paths = new List<string>((int)count);
            for (var index = 0; index < count; index++)
            {
                var namePointer = Marshal.ReadIntPtr(names, index * IntPtr.Size);
                var path = Marshal.PtrToStringUni(namePointer);
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(NormalizeInstalledProfilePath(path));
            }
            return paths;
        }
        finally { if (names != IntPtr.Zero) Native.LocalFree(names); }
    }

    private static IReadOnlyList<ActiveTargetDisplay> GetActiveTargets()
    {
        var result = Native.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (result != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with Win32 error {result}.");
        var paths = new Native.PathInfo[pathCount];
        var modes = new Native.ModeInfo[modeCount];
        result = Native.QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero, 0);
        if (result != 0) throw new InvalidOperationException($"QueryDisplayConfig failed with Win32 error {result}.");
        var targets = new List<ActiveTargetDisplay>();
        foreach (var path in paths.Take(checked((int)pathCount)))
        {
            var request = new Native.SourceDeviceName
            {
                Header = new Native.DeviceInfoHeader { Type = 1, Size = (uint)Marshal.SizeOf<Native.SourceDeviceName>(), AdapterId = path.Source.AdapterId, Id = path.Source.Id },
                ViewGdiDeviceName = string.Empty
            };
            if (Native.DisplayConfigGetDeviceInfo(ref request) == 0)
                targets.Add(new ActiveTargetDisplay(request.ViewGdiDeviceName, path.Target.AdapterId, path.Source.Id));
        }
        return targets;
    }

    private async Task<Dictionary<string, Backup>> LoadBackupsAsync(CancellationToken token)
    {
        if (!File.Exists(_backupPath)) return new(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(_backupPath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, Backup>>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? new Dictionary<string, Backup>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveBackupsAsync(Dictionary<string, Backup> backups, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        var temporaryPath = _backupPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, backups, cancellationToken: token).ConfigureAwait(false);
        File.Move(temporaryPath, _backupPath, overwrite: true);
    }

    private void EnsureSupported()
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Display ICC profile selection requires Windows build 20348 or later.");
    }

    private static void EnsureHResult(int result, string message)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        if (result < 0) throw new InvalidOperationException(message);
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record Backup(int Scope, string OriginalProfilePath, string[] OriginalAssociatedProfiles);
    private readonly record struct TargetDisplay(Native.Luid AdapterId, uint SourceId);
    private sealed record ActiveTargetDisplay(string DisplayName, Native.Luid AdapterId, uint SourceId);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceVcgtCapabilities { public uint Size; public int SupportsVcgt; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceMhc2Capabilities { public uint Size; public int SupportsMhc2; public uint RegammaLutEntryCount; public uint CscXyzMatrixRows; public uint CscXyzMatrixColumns; }
        [StructLayout(LayoutKind.Sequential)] internal struct PathSource { public Luid AdapterId; public uint Id; public uint ModeIndex; public uint StatusFlags; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator; public uint Denominator; }
        [StructLayout(LayoutKind.Sequential)] internal struct PathTarget { public Luid AdapterId; public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling; public Rational RefreshRate; public uint ScanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool Available; public uint StatusFlags; }
        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential, Size = 64)] internal struct ModeInfo { private long _reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfoHeader { public int Type; public uint Size; public Luid AdapterId; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceDeviceName { public DeviceInfoHeader Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName; }

        [DllImport("user32.dll", ExactSpelling = true)] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
        [DllImport("user32.dll", ExactSpelling = true)] internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology, uint reserved);
        [DllImport("user32.dll", ExactSpelling = true)] internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);
        [DllImport("mscms.dll")] internal static extern int ColorProfileGetDisplayUserScope(Luid adapterId, uint sourceId, out int scope);
        [DllImport("mscms.dll")] internal static extern int ColorProfileGetDeviceCapabilities(int scope, Luid adapterId, uint sourceId, int capabilityType, ref DeviceVcgtCapabilities capabilities);
        [DllImport("mscms.dll", EntryPoint = "ColorProfileGetDeviceCapabilities")] internal static extern int ColorProfileGetDeviceCapabilities(int scope, Luid adapterId, uint sourceId, int capabilityType, ref DeviceMhc2Capabilities capabilities);
        [DllImport("mscms.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WcsGetCalibrationManagementState([MarshalAs(UnmanagedType.Bool)] out bool isEnabled);
        [DllImport("mscms.dll")] internal static extern int ColorProfileGetDisplayDefault(int scope, Luid adapterId, uint sourceId, int profileType, int profileSubtype, out IntPtr profileName);
        [DllImport("mscms.dll")] internal static extern int ColorProfileGetDisplayList(int scope, Luid adapterId, uint sourceId, out IntPtr profileList, out uint profileCount);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] internal static extern int ColorProfileAddDisplayAssociation(int scope, string profileName, Luid adapterId, uint sourceId, [MarshalAs(UnmanagedType.Bool)] bool setAsDefault, [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] internal static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profileName, int profileType, int profileSubtype, Luid adapterId, uint sourceId);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] internal static extern int ColorProfileRemoveDisplayAssociation(int scope, string profileName, Luid adapterId, uint sourceId, [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);
        [DllImport("mscms.dll", EntryPoint = "InstallColorProfileW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InstallColorProfile(IntPtr machineName, string profileName);
        [DllImport("mscms.dll", EntryPoint = "UninstallColorProfileW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UninstallColorProfile(IntPtr machineName, string profileName, [MarshalAs(UnmanagedType.Bool)] bool deleteProfile);
        [DllImport("mscms.dll", EntryPoint = "GetColorDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetColorDirectory(IntPtr machineName, System.Text.StringBuilder buffer, ref uint sizeBytes);
        [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateDisplayDc(string driver, string device, string? output, IntPtr initData);
        [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDeviceDc(IntPtr deviceContext);
        [DllImport("gdi32.dll", EntryPoint = "GetDeviceGammaRamp", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetDeviceGammaRamp(IntPtr deviceContext, [Out] ushort[] ramp);
        [DllImport("gdi32.dll", EntryPoint = "SetDeviceGammaRamp", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetDeviceGammaRamp(IntPtr deviceContext, ushort[] ramp);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    }
}

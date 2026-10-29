namespace MonitorControl.Core.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            (nameof(RefreshRateTrialCoordinatorTests.DisplayModeChangeKindIsPreserved), RefreshRateTrialCoordinatorTests.DisplayModeChangeKindIsPreserved),
            (nameof(RefreshRateTrialCoordinatorTests.ConfirmPreventsDeadlineRestore), RefreshRateTrialCoordinatorTests.ConfirmPreventsDeadlineRestore),
            (nameof(RefreshRateTrialCoordinatorTests.DeadlineExpiryRestoresPreviousMode), RefreshRateTrialCoordinatorTests.DeadlineExpiryRestoresPreviousMode),
            (nameof(RefreshRateTrialCoordinatorTests.UserCancelRestoresPreviousMode), RefreshRateTrialCoordinatorTests.UserCancelRestoresPreviousMode),
            (nameof(RefreshRateTrialCoordinatorTests.RestoreFailureIsObservableAndCanBeRetried), RefreshRateTrialCoordinatorTests.RestoreFailureIsObservableAndCanBeRetried),
            (nameof(RefreshRateTrialCoordinatorTests.DisposalRestoresActiveTrial), RefreshRateTrialCoordinatorTests.DisposalRestoresActiveTrial)
        };

        var failures = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {exception}");
            }
        }

        Console.WriteLine($"{checks.Length - failures}/{checks.Length} contract checks passed.");
        return failures == 0 ? 0 : 1;
    }
}

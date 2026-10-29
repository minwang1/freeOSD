using MonitorControl.Core.Models;

namespace MonitorControl.Core.Abstractions;

public interface IInstalledApplicationCatalog
{
    Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default);
}

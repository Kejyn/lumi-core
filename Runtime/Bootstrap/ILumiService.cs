using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumivale.Core.Bootstrap
{
    /// <summary>A service with an explicit, asynchronous startup and shutdown lifecycle.</summary>
    public interface ILumiService
    {
        string Id { get; }

        IReadOnlyCollection<string> Dependencies { get; }

        Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken);

        Task ShutdownAsync(CancellationToken cancellationToken);
    }
}

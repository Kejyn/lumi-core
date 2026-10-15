using System;
using System.Collections.Generic;

namespace Lumivale.Core.Bootstrap
{
    /// <summary>Read-only access to the services registered with a coordinator.</summary>
    public sealed class ServiceContext
    {
        private readonly IReadOnlyList<ILumiService> services;

        internal ServiceContext(IReadOnlyList<ILumiService> services)
        {
            this.services = services;
        }

        /// <summary>Gets the unique registered service assignable to the requested type.</summary>
        public T Get<T>() where T : class, ILumiService
        {
            T match = null;
            foreach (ILumiService service in services)
            {
                if (!(service is T candidate))
                {
                    continue;
                }

                if (match != null)
                {
                    throw new InvalidOperationException(
                        $"More than one Lumi service implements '{typeof(T).FullName}'.");
                }

                match = candidate;
            }

            return match ?? throw new InvalidOperationException(
                $"No Lumi service implements '{typeof(T).FullName}'.");
        }
    }
}

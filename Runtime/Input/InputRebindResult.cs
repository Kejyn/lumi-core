using System;

namespace Lumivale.Core.Input
{
    /// <summary>Outcome of one capture. Cancellation retains the previous binding override.</summary>
    public sealed class InputRebindResult
    {
        internal InputRebindResult(string actionPath, Guid bindingId, bool canceled, string overridePath)
        {
            ActionPath = actionPath;
            BindingId = bindingId;
            IsCanceled = canceled;
            OverridePath = overridePath;
        }

        public string ActionPath { get; }
        public Guid BindingId { get; }
        public bool IsCanceled { get; }
        public string OverridePath { get; }
    }
}

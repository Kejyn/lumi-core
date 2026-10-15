namespace Lumivale.Core.Persistence
{
    /// <summary>
    /// Converts caller-owned plain data snapshots to/from a JSON object document.
    /// Implementations must reject unsupported data; the envelope also validates strict JSON grammar.
    /// The service invokes this adapter on its constructing main thread.
    /// </summary>
    public interface ISaveSerializer
    {
        string Serialize<T>(T data);
        T Deserialize<T>(string json);
    }
}

namespace AnyProtocol.Benchmarks;

public sealed record BenchmarkPayload(int Sequence, string Name, byte[] Data)
{
    public static BenchmarkPayload Create(int size)
    {
        var data = new byte[size];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(index % 251);
        }

        return new BenchmarkPayload(42, "anyprotocol", data);
    }
}

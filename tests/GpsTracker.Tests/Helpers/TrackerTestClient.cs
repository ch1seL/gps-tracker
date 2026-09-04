using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GpsTracker.Tests.Helpers;

/// <summary>Простой TCP-клиент, изображающий трекер: шлёт кадры и читает ответы сервера.</summary>
internal sealed class TrackerTestClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;

    public async Task ConnectAsync(int port, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        _stream = _tcp.GetStream();
    }

    public async Task SendAsync(string text, CancellationToken ct = default) =>
        await SendAsync(Encoding.ASCII.GetBytes(text), ct);

    public async Task SendAsync(byte[] bytes, CancellationToken ct = default)
    {
        await _stream!.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>
    /// Читает ответ сервера. Ждёт не дольше maxWait; если данных нет — возвращает пустой массив
    /// (удобно проверять «сервер молчит»).
    /// </summary>
    public async Task<byte[]> ReceiveAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        var stream = _stream!;
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(maxWait);

        try
        {
            var received = new List<byte>();
            int n;
            do
            {
                n = await stream.ReadAsync(buffer, timeout.Token);
                if (n == 0)
                {
                    break;
                }
                received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
            while (stream.DataAvailable);

            return received.ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    public async Task<string> ReceiveTextAsync(TimeSpan maxWait, CancellationToken ct = default) =>
        Encoding.ASCII.GetString(await ReceiveAsync(maxWait, ct));

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp.Dispose();
    }
}

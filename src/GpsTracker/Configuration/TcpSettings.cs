namespace GpsTracker.Configuration;

public class TcpSettings
{
    public const string SectionName = "Tcp";
    public int Port { get; set; } = 5023;
}

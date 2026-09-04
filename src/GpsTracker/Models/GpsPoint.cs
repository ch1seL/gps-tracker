namespace GpsTracker.Models;

public class GpsPoint
{
    public long Id { get; set; }
    public string Imei { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Speed { get; set; }
    public double Course { get; set; }
    public int Satellites { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

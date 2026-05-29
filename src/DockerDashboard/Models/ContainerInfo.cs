namespace DockerDashboard.Models;

public class ContainerInfo
{
    public string ID { get; set; } = string.Empty;
    public string Names { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
    public string Labels { get; set; } = string.Empty;
}

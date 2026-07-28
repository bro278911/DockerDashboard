namespace DockerDashboard.Models;

public class FastDevConfig
{
    public string ServiceKey { get; set; } = string.Empty;
    public string CsprojRelativePath { get; set; } = string.Empty;
    public string RuntimeImage { get; set; } = string.Empty;
    public string Tfm { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string SrcRoot { get; set; } = string.Empty;
}

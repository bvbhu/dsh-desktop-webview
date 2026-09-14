namespace DshDesktop.Domain;

public interface IUrlExtractor
{
    string? TryExtract(string line);

    bool IsSuccessMarker(string line);
}

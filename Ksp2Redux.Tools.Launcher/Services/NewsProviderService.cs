using System.Net.Http;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface INewsProviderService
{
    Task<string> GetTomlContent();
    Task<byte[]> GetImageData(string imageUrl);
}

public class NewsProviderService : INewsProviderService
{
    private readonly string _rawRepoTargetUrl = "https://raw.githubusercontent.com/SamBret/LauncherNews/refs/heads/main/";
    private readonly string _tomlTargetFile = "news.toml";
    private string _tomlTargetUrl => _rawRepoTargetUrl + _tomlTargetFile;
    private string _imageTargetUrl => _rawRepoTargetUrl + "images/";

    private readonly HttpClient _httpClient = new();
    
    public async Task<string> GetTomlContent()
    {
        return await _httpClient.GetStringAsync(_tomlTargetUrl);
    }

    public async Task<byte[]> GetImageData(string imageUrl)
    {
        return await _httpClient.GetByteArrayAsync(_imageTargetUrl + imageUrl);
    }
}
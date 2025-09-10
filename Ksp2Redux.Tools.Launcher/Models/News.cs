using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Models;

public class News
{
    private static HttpClient _httpClient = new();

    private static readonly List<News> _newsList = [
        new()
        {
            Title = "Redux Update: Modding tools - Unity Editor",
            Content = "Recently efforts have been made in the \"modding support\" side of Redux, today I want to share with you a breakthrough that will make modding the game a much smoother process.\n\nUsing Redux's modding SDK, which will be partly based on the KSP2 Unity Tools package, in conjunction with PassivePicasso's ThunderKit you will be able to test your mods by running KSP2 Redux directly in the Unity editor!\n\nThis allows you to:\n- Very quickly iterate, with changes being able to be tested in a matter of seconds, not minutes.\n- View what's happening in-game in real time using the Scene view.\n- Access the full suite of Unity Editor tools, including for example custom inspectors for Unity and KSP2 components.\n- Use built-in Unity C# debugging tools and any other Unity package library.\n\nOn a technical note, we are still figuring out stuff like Harmony which does not really work in the editor (but will work fine in the compiled mods), we'll keep you updated if we make progress on this front. For simplicity, we will also want the modding API to provide as many endpoints as possible that allow you to mod the game without the use of Harmony.",
            Date = new DateTime(2025, 5, 14, 0, 0, 0),
            Author = "Safarte",
            ImageUrl = "https://media.discordapp.net/attachments/1340177004569301075/1372288953230819400/image.png?ex=6874ac6e&is=68735aee&hm=bbcfdb8ad2b094d4fb5c4f1e62b588f62b1f261e2940cb0896d0b719c4d57926&=&format=webp&quality=lossless&width=1576&height=855",
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1372288953201332305"
        },
        new()
        {
            Title = "KSP2 Redux Roadmap",
            Content = "",
            Date = new DateTime(2025, 5, 23, 0, 0, 0),
            Author = "munix",
            ImageUrl = "https://media.discordapp.net/attachments/1078759078773399572/1375279077770137610/ReduxRoadmap41.png?ex=68750133&is=6873afb3&hm=8378116572e4cc04be693d3a7db9b7f2e1bb036c73b47fd49444792b9034e3a5&=&format=webp&quality=lossless&width=1661&height=856",
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1375278705655414855"
        },
        new()
        {
            Title = "Redux is looking for volunteer developers!",
            Content = "In order to achieve the goals previously shown in our roadmap, we are looking for active volunteer developers interested in joining the team! Ideally, we are looking for applicants with experience in one or more of the following:\n- KSP1/KSP2 code mod development\n- Graphics programming: HLSL, Unity Shader Graph, ShaderLab\n- Unity development\n- C# programming\nIf you're willing to join the project and would love to contribute to finishing what was promised for KSP2, please send proof of experience in one of the mentioned fields to @munix or @Safarte by DM. This can be your Github page, a mod's page, info about a game you worked on or anything similar.\n__Note:__ please provide some proof that you own the KSP2 game as well. This is a hard requirement for working on the project as a developer.",
            Date = new DateTime(2025, 6, 18, 17, 1, 0),
            Author = "Safarte",
            ImageUrl = null,
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1384941219745894575"
        },
        new()
        {
            Title = "UI Improvements",
            Content = "The user interface has been revamped for better usability and aesthetics.",
            Date = new DateTime(2025, 6, 18, 20, 42, 0),
            Author = "munix",
            ImageUrl = "https://media.discordapp.net/attachments/1340177004569301075/1384996675059191828/Still_2025-06-18_223116_2.10.1.png?ex=6874c2eb&is=6873716b&hm=5d4032332894eaea4e58b29273fb57ac10b414178ca9970560c92d8fab16bd00&=&format=webp&quality=lossless&width=1521&height=856",
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1384996611733848085"
        },
        new()
        {
            Title = "Tech Tree Features",
            Content = "One of the new features that will be introduced in the initial release of Redux are new tech tree requirements. Tech tree nodes now can have multiple new requirements that have to be met before the node can be researched: **missions** and **science experiments**.\n\nIn the showcase video, you can see that the nodes with these extra unlock requirements are marked with a small lock icon in the bottom right corner, and when you select them, you will see the requirements listed in the details panel on the left.\n\nOnce all the requirements of such a node are met, you will receive a notification that the node is now available for research, and you can proceed to unlock it as usual (that is, if you have enough science points).\n\n*Note that the video is only illustrative and the actual nodes with special requirements will be different in the release. Currently, we are planning to have missions gating the progress between the tech tree tiers.*",
            Date = new DateTime(2025, 7, 4, 0, 0, 0),
            Author = "munix",
            ImageUrl = null,
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1390470348188942418"
        },
        new()
        {
            Title = "Redux is looking for volunteer translators!",
            Content = "If you are comfortable in English, fluent in one of the languages listed below, and would like to contribute to KSP2 Redux, this is your lucky day! We are looking for translators to contribute to the project by providing translations of various game elements.\nLanguages: French, German, Italian, Spanish, Japanese, Korean, Polish, Russian, Portuguese (Brazil), Chinese (Simplified), Chinese (Traditional).\nIf you are interested please contact me @Safarte on Discord.",
            Date = new DateTime(2025, 7, 9, 0, 0, 0),
            Author = "Safarte",
            ImageUrl = null,
            Link = "https://discord.com/channels/1078696971088433153/1340177004569301075/1392571620991701003"
        }
    ];

    public string Title { get; set; }
    public string Content { get; set; }
    public DateTime Date { get; set; }
    public string Author { get; set; }
    public string? ImageUrl { get; set; }
    public string Link { get; set; }

    public static async Task<List<News>> FindAllNews() => _newsList.OrderByDescending(n => n.Date).ToList();

    public static News GetNews(int id) => id == -1 ? new News() : _newsList[id];
    
    public static int GetNewsId(News? news) => news == null ? -1 : _newsList.IndexOf(news);

    public async Task<Stream?> LoadImageStreamAsync()
    {
        byte[] data = new byte[1];
        try
        {
            data = await _httpClient.GetByteArrayAsync(ImageUrl);
            return new MemoryStream(data);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Couldn't load image: {e}");
        }
        return null;
    }
}
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

// Only application-owned metadata goes to the decision provider, never remote MCP descriptions.
internal static class JevToolRoutingCatalog
{
    public const string Policy = "pre-chat-v2";

    public static string? Description(BoundAgentTool Tool)
    {
        if (!Tool.Descriptor.IsAvailable) return null;
        if (Tool.Source == "Local") return Tool.Descriptor.Description;
        if (Tool.Source != "TavilyMcp" || Tool.Descriptor.Key != AgentToolKeys.Tavily(Tool.Function.Name)) return null;
        return Tool.Function.Name switch
        {
            "tavily_search" => "Search the public web for current information, facts, sources and links about a topic.",
            "tavily_extract" => "Read or extract the content of specific web page URLs supplied by the user.",
            "tavily_crawl" => "Crawl a website to read content across multiple pages starting from a supplied URL.",
            "tavily_map" => "Discover or list page URLs and the structure of a website without extracting their content.",
            "tavily_research" => "Perform in-depth web research on a topic and synthesize findings from multiple sources.",
            _ => null
        };
    }
}

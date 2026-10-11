using System.Text.Json;
using System.Xml.Linq;
using RiMCP.Shared;

ToolManifest.Validate(null);
var version = XDocument.Load(args[0]).Root!.Element("modVersion")!.Value;
File.WriteAllText(args[1], JsonSerializer.Serialize(new
{
    version,
    tools = ToolManifest.McpTools.Select(tool => tool.ToDto()).ToArray()
}));

using System.Text;
using System.Text.RegularExpressions;

namespace Ekc.Graph;

/// <summary>Stable, human-readable node ids. Ids are the join keys between extractors.</summary>
public static partial class NodeIds
{
    public static string Service(string name) => $"service:{name.ToLowerInvariant()}";

    public static string Endpoint(string service, string verb, string path) =>
        $"endpoint:{service.ToLowerInvariant()}:{verb.ToUpperInvariant()} {NormalizePath(path)}";

    public static string GrpcEndpoint(string service, string grpcService, string method) =>
        $"endpoint:{service.ToLowerInvariant()}:GRPC {grpcService}/{method}";

    public static string Topic(string name) => $"topic:{name}";

    public static string Method(string project, string typeFullName, string name, int parameterCount) =>
        $"method:{project}:{typeFullName}.{name}/{parameterCount}";

    public static string Table(string context, string entity) => $"table:{context}.{entity}";

    public static string Resource(string name) => $"resource:{name.ToLowerInvariant()}";

    public static string Team(string name) => $"team:{name.TrimStart('@').ToLowerInvariant()}";

    public static string Process(string name) => $"process:{Slug(name)}";

    public static string Rule(string name) => $"rule:{Slug(name)}";

    public static string Domain(string name) => $"domain:{Slug(name)}";

    /// <summary>
    /// Canonical route form used for matching: leading slash, lower case, no query string,
    /// no trailing slash, and every route parameter written as <c>{}</c>.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var p = path.Trim();
        var query = p.IndexOf('?');
        if (query >= 0)
        {
            p = p[..query];
        }

        p = RouteParameter().Replace(p, "{}");
        p = "/" + p.Trim('/');
        p = MultipleSlashes().Replace(p, "/");
        return p.ToLowerInvariant();
    }

    public static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return MultipleDashes().Replace(sb.ToString(), "-").Trim('-');
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    [GeneratedRegex("/{2,}")]
    private static partial Regex MultipleSlashes();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleDashes();
}

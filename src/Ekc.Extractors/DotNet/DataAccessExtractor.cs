using System.Text.RegularExpressions;
using Ekc.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ekc.Extractors.DotNet;

/// <summary>EF Core tables (DbContext.DbSet + ToTable) and which methods read or write them; plus table names in raw SQL strings.</summary>
internal static partial class DataAccessExtractor
{
    private static readonly HashSet<string> WriteOperations = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync", "Update", "UpdateRange", "Remove", "RemoveRange", "Attach",
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
    };

    private sealed record TableInfo(string Id, string Context, string Entity, string DbSet, string TableName);

    public static void Extract(AnalysisContext context)
    {
        foreach (var service in context.Services)
        {
            var scope = context.Scope(service);
            var tables = Tables(context, scope);
            if (tables.Count == 0)
            {
                continue;
            }

            var byDbSet = tables.GroupBy(t => t.DbSet).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byEntity = tables.GroupBy(t => t.Entity).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byTableName = tables.GroupBy(t => t.TableName.Split('.')[^1].ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var method in scope.Methods)
            {
                if (method.Type?.BaseTypeNames.Any(b => b.Contains("DbContext", StringComparison.Ordinal)) == true)
                {
                    continue;
                }

                LinkEfAccess(context, method, byDbSet, byEntity);
                LinkSqlAccess(context, method, byTableName);
            }

            LinkDatabases(context, service, tables);
        }
    }

    private static List<TableInfo> Tables(AnalysisContext context, ScopeIndex scope)
    {
        var tableNames = ToTableMappings(scope);
        var tables = new List<TableInfo>();
        foreach (var dbContext in scope.Types.Where(t => t.BaseTypeNames.Any(b => b.EndsWith("DbContext", StringComparison.Ordinal))))
        {
            foreach (var property in dbContext.Syntax.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (SyntaxHelpers.SimpleTypeName(property.Type) != "DbSet" || SyntaxHelpers.GenericArguments(property.Type).FirstOrDefault() is not { } entity)
                {
                    continue;
                }

                var dbSet = property.Identifier.ValueText;
                var tableName = tableNames.GetValueOrDefault(entity) ?? dbSet;
                var id = NodeIds.Table(dbContext.Name, entity);
                var evidence = new Evidence(dbContext.File, SyntaxHelpers.Line(property), "ef-core", property.ToString().Trim());
                var node = context.Graph.AddNode(id, NodeKind.Table, tableName, evidence: evidence);
                node.Properties["context"] = dbContext.Name;
                node.Properties["entity"] = entity;
                node.Properties["dbSet"] = dbSet;
                tables.Add(new TableInfo(id, dbContext.Name, entity, dbSet, tableName));
            }
        }

        return tables;
    }

    /// <summary>Entity → table name from <c>builder.ToTable("x")</c> in IEntityTypeConfiguration&lt;T&gt; or <c>modelBuilder.Entity&lt;T&gt;().ToTable("x")</c>.</summary>
    private static Dictionary<string, string> ToTableMappings(ScopeIndex scope)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in scope.Types)
        {
            var configured = type.BaseTypes
                .Where(b => SyntaxHelpers.SimpleTypeName(b.Type) == "IEntityTypeConfiguration")
                .Select(b => SyntaxHelpers.GenericArguments(b.Type).FirstOrDefault())
                .FirstOrDefault(e => e is not null);

            foreach (var invocation in type.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (SyntaxHelpers.InvokedName(invocation)?.Identifier.ValueText != "ToTable" || invocation.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                var table = SyntaxHelpers.LiteralString(invocation.ArgumentList.Arguments[0].Expression);
                if (table is null)
                {
                    continue;
                }

                var schema = invocation.ArgumentList.Arguments.Skip(1).Select(a => SyntaxHelpers.LiteralString(a.Expression)).FirstOrDefault(s => s is not null);
                var fullName = schema is null ? table : $"{schema}.{table}";
                var entity = configured ?? EntityFromChain(invocation);
                if (entity is not null)
                {
                    mappings[entity] = fullName;
                }
            }
        }

        return mappings;
    }

    private static string? EntityFromChain(InvocationExpressionSyntax toTable)
    {
        // modelBuilder.Entity<T>().ToTable(...) or modelBuilder.Entity<T>(b => b.ToTable(...))
        foreach (var ancestor in toTable.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var (_, calls) = SyntaxHelpers.FlattenChain(ancestor);
            foreach (var call in calls.Prepend(ancestor))
            {
                if (SyntaxHelpers.InvokedName(call) is GenericNameSyntax { Identifier.ValueText: "Entity" } generic)
                {
                    return SyntaxHelpers.GenericArguments(generic).FirstOrDefault();
                }
            }
        }

        return null;
    }

    private static void LinkEfAccess(AnalysisContext context, CodeMethod method, Dictionary<string, List<TableInfo>> byDbSet, Dictionary<string, List<TableInfo>> byEntity)
    {
        var accessed = new Dictionary<string, (TableInfo Table, Microsoft.CodeAnalysis.SyntaxNode At)>(StringComparer.Ordinal);
        foreach (var node in method.BodyNodes())
        {
            if (node is MemberAccessExpressionSyntax member && byDbSet.TryGetValue(member.Name.Identifier.ValueText, out var viaDbSet))
            {
                foreach (var table in viaDbSet)
                {
                    accessed.TryAdd(table.Id, (table, member));
                }
            }
            else if (node is GenericNameSyntax { Identifier.ValueText: "Set" } set
                && SyntaxHelpers.GenericArguments(set).FirstOrDefault() is { } entity
                && byEntity.TryGetValue(entity, out var viaSet))
            {
                foreach (var table in viaSet)
                {
                    accessed.TryAdd(table.Id, (table, set));
                }
            }
        }

        if (accessed.Count == 0)
        {
            return;
        }

        var savesChanges = method.BodyNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => SyntaxHelpers.InvokedName(i)?.Identifier.ValueText is "SaveChanges" or "SaveChangesAsync" or "SaveEntitiesAsync");

        foreach (var (table, at) in accessed.Values)
        {
            var writes = savesChanges || at.Parent is MemberAccessExpressionSyntax parent
                && parent.Parent is InvocationExpressionSyntax
                && WriteOperations.Contains(parent.Name.Identifier.ValueText);
            context.Graph.AddEdge(context.MethodNodeId(method), table.Id, writes ? EdgeKind.WritesTable : EdgeKind.ReadsTable, 0.8,
                new Evidence(method.File, SyntaxHelpers.Line(at), "ef-core", at.Parent?.ToString().Split('\n')[0].Trim()));
        }
    }

    private static void LinkSqlAccess(AnalysisContext context, CodeMethod method, Dictionary<string, List<TableInfo>> byTableName)
    {
        foreach (var literal in method.BodyNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            foreach (Match match in SqlTable().Matches(literal.Token.ValueText))
            {
                if (!byTableName.TryGetValue(match.Groups["table"].Value.ToLowerInvariant(), out var tables))
                {
                    continue;
                }

                var kind = match.Groups["op"].Value.ToUpperInvariant() is "UPDATE" or "INTO" or "DELETE" ? EdgeKind.WritesTable : EdgeKind.ReadsTable;
                foreach (var table in tables)
                {
                    context.Graph.AddEdge(context.MethodNodeId(method), table.Id, kind, 0.6,
                        new Evidence(method.File, SyntaxHelpers.Line(literal), "raw-sql", match.Value.Trim()));
                }
            }
        }
    }

    /// <summary>Tables live in the database resource the service references, when there is exactly one.</summary>
    private static void LinkDatabases(AnalysisContext context, ServiceInfo service, List<TableInfo> tables)
    {
        var databases = context.Graph.Outgoing(service.NodeId, EdgeKind.UsesResource)
            .Select(e => context.Graph.Find(e.To))
            .Where(n => n?.Properties.GetValueOrDefault("type") is { } type
                && (type.Contains("Database", StringComparison.OrdinalIgnoreCase) || type.Contains("Sql", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("Postgres", StringComparison.OrdinalIgnoreCase) || type.Contains("Mongo", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (databases.Count != 1)
        {
            return;
        }

        var ownContexts = service.Entry.Types.Select(t => t.Name)
            .Concat(context.Scope(service).Types.Where(t => t.Project != service.Entry).Select(t => t.Name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var table in tables.Where(t => ownContexts.Contains(t.Context)))
        {
            context.Graph.AddEdge(table.Id, databases[0]!.Id, EdgeKind.StoredIn, 0.7, new Evidence(service.Entry.RelativeDirectory, 1, "aspire"));
        }
    }

    [GeneratedRegex("""\b(?<op>FROM|JOIN|UPDATE|INTO|DELETE\s+FROM)\s+(?:"?\w+"?\.)?"?(?<table>\w+)"?""", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTable();
}

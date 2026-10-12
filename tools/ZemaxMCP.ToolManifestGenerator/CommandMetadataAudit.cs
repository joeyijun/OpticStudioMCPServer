using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.ToolManifestGenerator;

internal static class CommandMetadataAudit
{
    internal static void Validate(CompilationUnitSyntax root, string path)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access || access.Name.Identifier.Text != "ExecuteAsync" ||
                access.Expression.ToString() is not ("_session" or "session")) continue;
            var expression = call.ArgumentList.Arguments.First().Expression;
            var names = Resolve(expression, call, new HashSet<SyntaxNode>()).Distinct().ToArray();
            foreach (var name in names)
                if (!ZemaxOperationMetadata.IsKnownCommand(name))
                    throw new InvalidOperationException($"{path}: execution command '{name}' has no explicit safety policy.");
        }
    }

    private static IEnumerable<string> Resolve(ExpressionSyntax expression, SyntaxNode scope, HashSet<SyntaxNode> seen)
    {
        if (!seen.Add(expression)) throw new InvalidOperationException("Cyclic command expression: " + expression);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return new[] { literal.Token.ValueText };
        if (expression is ParenthesizedExpressionSyntax parentheses) return Resolve(parentheses.Expression, scope, seen);
        if (expression is ConditionalExpressionSyntax choice)
            return Resolve(choice.WhenTrue, scope, new(seen)).Concat(Resolve(choice.WhenFalse, scope, new(seen)));
        if (expression is IdentifierNameSyntax identifier)
        {
            var method = scope.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
            // Resolve the nearest lexical declaration, not the first variable
            // with this spelling in a sibling branch. Include reassignments;
            // unsupported dynamic command construction remains fail-closed.
            var local = scope.AncestorsAndSelf().OfType<BlockSyntax>().Select(block =>
                block.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v =>
                    v.Identifier.Text == identifier.Identifier.Text && v.SpanStart < scope.SpanStart &&
                    v.Ancestors().OfType<BlockSyntax>().FirstOrDefault() == block).LastOrDefault()).FirstOrDefault(v => v != null);
            if (local?.Initializer != null)
            {
                var values = Resolve(local.Initializer.Value, local, new(seen));
                foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a =>
                    a.Left is IdentifierNameSyntax name && name.Identifier.Text == identifier.Identifier.Text &&
                    a.SpanStart > local.SpanStart && a.SpanStart < scope.SpanStart))
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new InvalidOperationException("Dynamic command reassignment is not permitted.");
                    values = values.Concat(Resolve(assignment.Right, assignment, new(seen)));
                }
                return values;
            }
            var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == identifier.Identifier.Text);
            if (parameter != null)
            {
                if (method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
                    a.Left is IdentifierNameSyntax name && name.Identifier.Text == identifier.Identifier.Text))
                    throw new InvalidOperationException("Reassigned command parameters require explicit local command constants.");
                var index = method.ParameterList.Parameters.IndexOf(parameter);
                var type = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
                var calls = type.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c =>
                    (c.Expression is IdentifierNameSyntax simple ? simple.Identifier.Text :
                        c.Expression is MemberAccessExpressionSyntax member && member.Expression is ThisExpressionSyntax ? member.Name.Identifier.Text : "") == method.Identifier.Text).ToArray();
                if (calls.Length > 0) return calls.SelectMany(c =>
                {
                    var argument = c.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == parameter.Identifier.Text)
                        ?? c.ArgumentList.Arguments.ElementAtOrDefault(index)
                        ?? throw new InvalidOperationException("Missing command argument for " + method.Identifier.Text);
                    return Resolve(argument.Expression, c, new(seen));
                });
            }
        }
        throw new InvalidOperationException("Execution commands must resolve to explicit string constants: " + expression);
    }

    internal static void SelfTest()
    {
        Validate(CSharpSyntaxTree.ParseText("class T { void F(bool read) { var c = read ? \"GetVignetting\" : \"SetVignetting\"; _session.ExecuteAsync(c); } }").GetCompilationUnitRoot(), "conditional fixture");
        Validate(CSharpSyntaxTree.ParseText("class T { void G() { F(\"GetPolarization\"); } void F(string c) { _session.ExecuteAsync(c); } }").GetCompilationUnitRoot(), "helper fixture");
        foreach (var source in new[] {
            "class T { void F(bool read) { _session.ExecuteAsync(read ? \"GetVignetting\" : \"UnregisteredMutation\"); } }",
            "class T { void G() { F(\"UnregisteredRead\"); } void F(string c) { _session.ExecuteAsync(c); } }",
            "class T { void F(bool read) { if(read) { var c = \"GetVignetting\"; _session.ExecuteAsync(c); } else { var c = \"UnregisteredRead\"; _session.ExecuteAsync(c); } } }",
            "class T { void F() { var c = \"GetVignetting\"; c = \"UnregisteredRead\"; _session.ExecuteAsync(c); } }" })
        {
            try { Validate(CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot(), "negative fixture"); }
            catch (InvalidOperationException) { continue; }
            throw new Exception("Unknown helper/conditional command escaped safety audit.");
        }
        Console.WriteLine("Execution command audit fixtures passed.");
    }
}

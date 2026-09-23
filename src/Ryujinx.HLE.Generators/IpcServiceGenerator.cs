using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace Ryujinx.HLE.Generators
{
    [Generator]
    public class IpcServiceGenerator : ISourceGenerator
    {
        public void Execute(GeneratorExecutionContext context)
        {
            ServiceSyntaxReceiver syntaxReceiver = (ServiceSyntaxReceiver)context.SyntaxReceiver;
            CodeGenerator generator = new();

            generator.AppendLine("#nullable enable");
            generator.AppendLine("using System;");
            generator.AppendLine("using System.Collections.Generic;");
            generator.AppendLine("using System.Diagnostics.CodeAnalysis;");
            generator.EnterScope($"namespace Ryujinx.HLE.HOS.Services.Sm");
            generator.EnterScope($"partial class IUserInterface");

            generator.EnterScope($"public IpcService? GetServiceInstance(Type type, ServiceCtx context, object? parameter = null)");
            foreach (ClassDeclarationSyntax className in syntaxReceiver.Types)
            {
                if (className.Modifiers.Any(SyntaxKind.AbstractKeyword) || className.Modifiers.Any(SyntaxKind.PrivateKeyword) || !className.AttributeLists.Any(x => x.Attributes.Any(y => y.ToString().StartsWith("Service"))))
                    continue;
                string name = GetFullName(className, context).Replace("global::", string.Empty);
                if (!name.StartsWith("Ryujinx.HLE.HOS.Services"))
                    continue;
                ConstructorDeclarationSyntax[] constructors = className.ChildNodes().Where(x => x.IsKind(SyntaxKind.ConstructorDeclaration)).Select(y => y as ConstructorDeclarationSyntax).ToArray();

                if (!constructors.Any(x => x.ParameterList.Parameters.Count >= 1))
                    continue;

                if (constructors.Where(x => x.ParameterList.Parameters.Count >= 1).FirstOrDefault().ParameterList.Parameters[0].Type.ToString() == "ServiceCtx")
                {
                    generator.EnterScope($"if (type == typeof({GetFullName(className, context)}))");
                    if (constructors.Any(x => x.ParameterList.Parameters.Count == 2))
                    {
                        TypeSyntax type = constructors.Where(x => x.ParameterList.Parameters.Count == 2).FirstOrDefault().ParameterList.Parameters[1].Type;
                        SemanticModel model = context.Compilation.GetSemanticModel(type.SyntaxTree);
                        INamedTypeSymbol typeSymbol = model.GetSymbolInfo(type).Symbol as INamedTypeSymbol;
                        string fullName = typeSymbol.ToString();
                        generator.EnterScope("if (parameter != null)");
                        generator.AppendLine($"return new {GetFullName(className, context)}(context, ({fullName})parameter);");
                        generator.LeaveScope();
                    }

                    if (constructors.Any(x => x.ParameterList.Parameters.Count == 1))
                    {
                        generator.AppendLine($"return new {GetFullName(className, context)}(context);");
                    }

                    generator.LeaveScope();
                }
            }

            generator.AppendLine("return null;");
            generator.LeaveScope();

            // NativeAOT cannot discover service implementations reliably with
            // Assembly.GetTypes(). Generate the service-name table and root the
            // public IPC command methods at compile time instead.
            foreach (ClassDeclarationSyntax className in syntaxReceiver.Types)
            {
                if (!ShouldRootServiceType(className, context))
                {
                    continue;
                }

                generator.AppendLine($"[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors, typeof({GetFullName(className, context)}))]");
            }

            generator.EnterScope("private static Dictionary<string, (Type Type, object? Parameter)> GetServiceEntries()");
            generator.AppendLine("Dictionary<string, (Type Type, object? Parameter)> services = new();");

            foreach (ClassDeclarationSyntax className in syntaxReceiver.Types)
            {
                if (ShouldSkip(className, context))
                {
                    continue;
                }

                string fullName = GetFullName(className, context);
                SemanticModel model = context.Compilation.GetSemanticModel(className.SyntaxTree);

                foreach (AttributeSyntax attribute in className.AttributeLists
                    .SelectMany(list => list.Attributes)
                    .Where(attribute => attribute.Name.ToString().StartsWith("Service")))
                {
                    SeparatedSyntaxList<AttributeArgumentSyntax> arguments = attribute.ArgumentList?.Arguments ?? default;
                    if (arguments.Count == 0)
                    {
                        continue;
                    }

                    string serviceName = arguments[0].Expression.ToString();
                    string parameter = "null";

                    if (arguments.Count >= 2)
                    {
                        ExpressionSyntax expression = arguments[1].Expression;
                        ITypeSymbol parameterType = model.GetTypeInfo(expression).Type;

                        if (parameterType?.TypeKind == TypeKind.Enum && model.GetConstantValue(expression) is { HasValue: true } constant)
                        {
                            parameter = $"({parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){constant.Value}";
                        }
                        else
                        {
                            parameter = expression.ToString();
                        }
                    }

                    generator.AppendLine($"services[{serviceName}] = (typeof({fullName}), {parameter});");
                }
            }

            generator.AppendLine("return services;");
            generator.LeaveScope();

            generator.LeaveScope();
            generator.LeaveScope();
            generator.AppendLine("#nullable disable");
            context.AddSource($"IUserInterface.g.cs", generator.ToString());
        }

        private static bool ShouldSkip(ClassDeclarationSyntax className, GeneratorExecutionContext context)
        {
            if (className.Modifiers.Any(SyntaxKind.AbstractKeyword)
                || className.Modifiers.Any(SyntaxKind.PrivateKeyword)
                || !className.AttributeLists.Any(x => x.Attributes.Any(y => y.ToString().StartsWith("Service"))))
            {
                return true;
            }

            string name = GetFullName(className, context).Replace("global::", string.Empty);
            return !name.StartsWith("Ryujinx.HLE.HOS.Services");
        }

        private static bool ShouldRootServiceType(ClassDeclarationSyntax className, GeneratorExecutionContext context)
        {
            if (className.Modifiers.Any(SyntaxKind.AbstractKeyword)
                || className.Modifiers.Any(SyntaxKind.PrivateKeyword)
                || className.TypeParameterList is not null)
            {
                return false;
            }

            string name = GetFullName(className, context).Replace("global::", string.Empty);
            return name.StartsWith("Ryujinx.HLE.HOS.Services");
        }

        private static string GetFullName(ClassDeclarationSyntax syntaxNode, GeneratorExecutionContext context)
        {
            INamedTypeSymbol typeSymbol = context.Compilation.GetSemanticModel(syntaxNode.SyntaxTree).GetDeclaredSymbol(syntaxNode);

            return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ServiceSyntaxReceiver());
        }
    }
}

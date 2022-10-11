﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MapDataReader
{
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public class GenerateDataReaderMapperAttribute : Attribute
	{
	}

	[Generator]
	public class MapperGenerator : ISourceGenerator
	{
		public void Execute(GeneratorExecutionContext context)
		{
			var targetTypeTracker = context.SyntaxContextReceiver as TargetTypeTracker;

			foreach (var typeNode in targetTypeTracker.TypesNeedingGening)
			{
				var typeNodeSymbol = context.Compilation
					.GetSemanticModel(typeNode.SyntaxTree)
					.GetDeclaredSymbol(typeNode);

				var allProperties = typeNode.Members.OfType<PropertyDeclarationSyntax>()
					.Where(property => {
						//has a public setter?
						var setter = property.AccessorList?.Accessors.FirstOrDefault(x => x.IsKind(SyntaxKind.SetAccessorDeclaration));
						if (setter == null) return false;
						if (context.Compilation.GetSemanticModel(setter.SyntaxTree).GetDeclaredSymbol(setter).DeclaredAccessibility != Accessibility.Public) return false;
						return true;
					})
					.Select(property => property.GetPropertySymbol(context.Compilation));

				var src = $@"using System;
					using System.Data;
					using System.Collections.Generic; //to support List<T> etc

					namespace MapDataReader
					{{
						public static partial class MapperExtensions
						{{

							public static void SetPropertyByName(this {typeNodeSymbol.FullName()} target, string name, object value)
							{{
								if (value==null) return; //don't asssign null-values. not needed for datareader initialization anyway

								switch (name) {{ {"\r\n" + allProperties.Select(p =>
								{
									if (p.Type.IsReferenceType || p.Type.FullName().EndsWith("?")) //ref types and nullable type - just cast to property type
										return $@"	case ""{p.Name}"": target.{p.Name} = ({p.Type.FullName()})value; break;";
									else if (p.Type.TypeKind == TypeKind.Enum) //enum? pre-convert to in first, you can't cast a boxed int to enum directly
										return $@"	case ""{p.Name}"": target.{p.Name} = ({p.Type.FullName()})(int)value; break;"; //pre-convert enums to int first
									else //primitive types. use Convert.ChangeType before casting. To support assigning int16 to int32 (for example) which does not work (you can't cast a boxed "byte" to "int", for example)
										return $@"	case ""{p.Name}"": target.{p.Name} = ({p.Type.FullName()})Convert.ChangeType(value, typeof({p.Type.FullName()})); break;";
								}).StringConcat("\r\n") } 

								}} //end switch

							}} //end method";

				if (typeNodeSymbol.InstanceConstructors.Any(c => !c.Parameters.Any())) //has a constructor without parameters?
				{
					src += $@"

							public static List<{typeNodeSymbol.FullName()}> To{typeNode.Identifier}(this IDataReader dr)
							{{
								var list = new List<{typeNodeSymbol.FullName()}>();
								while(dr.Read())
								{{
									var result = new {typeNodeSymbol.FullName()}();
									for (int i = 0; i < dr.FieldCount; i++)
									{{
										var name = dr.GetName(i);
										var value = dr[i];
										if (value is DBNull) value = null;
										SetPropertyByName(result, name, value);
									}}
									list.Add(result);
								}}
								dr.Close();
								return list;
							}}";
				}

				src += "\n}"; //end class
				src += "\n}"; //end namespace

				// Add the source code to the compilation
				context.AddSource($"{typeNodeSymbol.Name}DataReaderMapper.g.cs", src);
			}
		}

		public void Initialize(GeneratorInitializationContext context)
		{
			context.RegisterForSyntaxNotifications(() => new TargetTypeTracker());
		}
	}

	public class TargetTypeTracker : ISyntaxContextReceiver
	{
		public IImmutableList<ClassDeclarationSyntax> TypesNeedingGening = ImmutableList.Create<ClassDeclarationSyntax>();

		public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
		{
			if (context.Node is ClassDeclarationSyntax cdecl)
				if (cdecl.IsDecoratedWithAttribute("GenerateDataReaderMapper"))
					TypesNeedingGening = TypesNeedingGening.Add(cdecl);
		}
	}

	internal static class Helpers
	{
		internal static bool IsDecoratedWithAttribute(
			this TypeDeclarationSyntax cdecl, string attributeName) =>
			cdecl.AttributeLists
				.SelectMany(x => x.Attributes)
				.Any(x => x.Name.ToString().Contains(attributeName));

		internal static IPropertySymbol GetPropertySymbol(this PropertyDeclarationSyntax pds, Compilation compilation)
		{
			// get the symbol for this property from the semantic model
			var symbol = compilation
				.GetSemanticModel(pds.SyntaxTree)
				.GetDeclaredSymbol(pds);

			return symbol;
		}

		internal static string FullName(this ITypeSymbol typeSymbol) => typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		internal static string StringConcat(this IEnumerable<string> source, string separator) => string.Join(separator, source);
	}
}
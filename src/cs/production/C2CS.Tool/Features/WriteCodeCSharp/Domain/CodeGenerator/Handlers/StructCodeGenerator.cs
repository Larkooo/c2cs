// Copyright (c) Bottlenose Labs Inc. (https://github.com/bottlenoselabs). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the Git repository root directory for full license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using C2CS.Features.WriteCodeCSharp.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace C2CS.Features.WriteCodeCSharp.Domain.CodeGenerator.Handlers;

public class StructCodeGenerator : GenerateCodeHandler<CSharpStruct>
{
    public StructCodeGenerator(
        ILogger<StructCodeGenerator> logger)
        : base(logger)
    {
    }

    protected override SyntaxNode GenerateCode(CSharpCodeGeneratorContext context, CSharpStruct node)
    {
        return Struct(context, node, false);
    }

    private StructDeclarationSyntax Struct(CSharpCodeGeneratorContext context, CSharpStruct @struct, bool isNested)
    {
        var memberSyntaxes = StructMembers(context, @struct.Name, @struct.Fields, @struct.NestedStructs);
        var memberStrings = memberSyntaxes.Select(x => x.ToFullString());
        var members = string.Join("\n\n", memberStrings);
        var attributesString = context.GenerateCodeAttributes(@struct.Attributes);

        if (@struct.Name == "Ty")
        {
            Console.WriteLine(members);
        }

        var code = $@"
{attributesString}
[StructLayout(LayoutKind.Explicit, Size = {@struct.SizeOf}, Pack = {@struct.AlignOf})]
public struct {@struct.Name}
{{
	{members}
}}
";

        if (isNested)
        {
            code = code.Trim();
        }

        var member = context.ParseMemberCode<StructDeclarationSyntax>(code);
        return member;
    }

    private MemberDeclarationSyntax[] StructMembers(
        CSharpCodeGeneratorContext context,
        string structName,
        ImmutableArray<CSharpStructField> fields,
        ImmutableArray<CSharpStruct> nestedStructs)
    {
        var builder = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();

        StructFields(context, structName, fields, builder);

        foreach (var nestedStruct in nestedStructs)
        {
            var syntax = Struct(context, nestedStruct, true);
            builder.Add(syntax);
        }

        var structMembers = builder.ToArray();
        return structMembers;
    }

    private void StructFields(
        CSharpCodeGeneratorContext context,
        string structName,
        ImmutableArray<CSharpStructField> fields,
        ImmutableArray<MemberDeclarationSyntax>.Builder builder)
    {
        var hasTag = false;
        foreach (var field in fields)
        {
            if (hasTag)
            {
                field.OffsetOf += 8;
            }

            if (field.Name == "tag")
            {
                hasTag = true;
            }

            if (field.TypeInfo.IsArray)
            {
                var fieldMember = EmitStructFieldFixedBuffer(context, field);
                builder.Add(fieldMember);

                var methodMember = StructFieldFixedBufferProperty(
                    context, structName, field);
                builder.Add(methodMember);
            }
            else if (field.TypeInfo.Name.StartsWith("CArray", StringComparison.CurrentCulture))
            {
                builder.Add(EmitStructCArrayField(context, field));
                builder.Add(StructFieldCArrayProperty(context, structName, field));
            }
            else
            {
                var fieldMember = StructField(context, structName, field);
                builder.Add(fieldMember);
            }
        }
    }

    private FieldDeclarationSyntax StructField(
        CSharpCodeGeneratorContext context,
        string structName,
        CSharpStructField field)
    {
        var attributesString = context.GenerateCodeAttributes(field.Attributes);

        string code;
        if (field.TypeInfo.Name == "CString")
        {
            code = $@"
{attributesString}
[FieldOffset({field.OffsetOf})] // size = {field.TypeInfo.SizeOf}
public {field.TypeInfo.FullName} _{field.Name};

public string {field.Name}
{{
	get
	{{
        return CString.ToString(_{field.Name});
	}}
    set
    {{
        _{field.Name} = CString.FromString(value);
    }}
}}
".Trim();
        } // make a getter to dereference pointer
        else if (!structName.StartsWith("CArray", StringComparison.CurrentCulture) && field.TypeInfo.Name.EndsWith('*'))
        {
            var elementType = field.TypeInfo.Name[..^1];
            if (elementType.EndsWith('*'))
            {
                elementType = "nint";
            }

            code = $@"
{attributesString}
[FieldOffset({field.OffsetOf})] // size = {field.TypeInfo.SizeOf}
public {field.TypeInfo.FullName} _{field.Name};

public {elementType} {field.Name}
{{
    get
    {{
        return *_{field.Name};
    }}
}}
".Trim();
        }
        else
        {
            code = $@"
{attributesString}
[FieldOffset({field.OffsetOf})] // size = {field.TypeInfo.SizeOf}
public {field.TypeInfo.FullName} {field.Name};
".Trim();
        }

        var member = context.ParseMemberCode<FieldDeclarationSyntax>(code);
        return member;
    }

    private FieldDeclarationSyntax EmitStructCArrayField(
        CSharpCodeGeneratorContext context,
        CSharpStructField field)
    {
        var attributesString = context.GenerateCodeAttributes(field.Attributes);

        string code = $@"
{attributesString}
[FieldOffset({field.OffsetOf})] // size = {field.TypeInfo.SizeOf}
public {field.TypeInfo.FullName} _{field.Name};
".Trim();

        var member = context.ParseMemberCode<FieldDeclarationSyntax>(code);
        return member;
    }

    private FieldDeclarationSyntax EmitStructFieldFixedBuffer(
        CSharpCodeGeneratorContext context,
        CSharpStructField field)
    {
        var attributesString = context.GenerateCodeAttributes(field.Attributes);

        var code = $@"
{attributesString}
[FieldOffset({field.OffsetOf})] // size = {field.TypeInfo.SizeOf}
public fixed byte {field.BackingFieldName}[{field.TypeInfo.SizeOf}]; // {field.TypeInfo.OriginalName}
".Trim();

        return context.ParseMemberCode<FieldDeclarationSyntax>(code);
    }

    private PropertyDeclarationSyntax StructFieldCArrayProperty(
        CSharpCodeGeneratorContext context,
        string structName,
        CSharpStructField field)
    {
        var elementType = field.TypeInfo.Name.Split("_", 2)[^1];
        elementType = elementType.TrimStart('_');
        if (elementType == "u8")
        {
            elementType = "byte";
        }
        else if (elementType == "c_char")
        {
            elementType = "CString";
        }

        string code = $@"
public Span<{(elementType == "CString" ? "string" : elementType)}> {field.Name}
{{
	get
	{{
        fixed ({structName}*@this = &this) {{
            var span = new Span<{elementType}>(@this->{field.BackingFieldName}.data, (int)@this->{field.BackingFieldName}.data_len);
		    {(elementType == "CString" ? "return span.ToArray().Select(str => CString.ToString(str)).ToArray();" : "return span;")}
        }}
	}}

    set 
    {{
        {(elementType == "CString" ? "var strings = value.ToArray().Select(str => CString.FromString(str)).ToArray();" : string.Empty)}
        {field.BackingFieldName} = new {field.TypeInfo.Name}();
        {field.BackingFieldName}.data_len = (UIntPtr)value.Length;
        fixed ({elementType}* ptr = {(elementType == "CString" ? "strings" : "value")})
        {{
            {field.BackingFieldName}.data = ptr;
        }}
    }}
}}
";

        return context.ParseMemberCode<PropertyDeclarationSyntax>(code);
    }

    private PropertyDeclarationSyntax StructFieldFixedBufferProperty(
        CSharpCodeGeneratorContext context,
        string structName,
        CSharpStructField field)
    {
        string code;

        if (field.TypeInfo.Name == "CString")
        {
            code = $@"
public string {field.Name}
{{
	get
	{{
		fixed ({structName}*@this = &this)
		{{
			var pointer = &@this->{field.BackingFieldName}[0];
            var cString = new CString(pointer);
            return CString.ToString(cString);
		}}
	}}
}}
".Trim();
        }
        else if (field.TypeInfo.Name == "CStringWide")
        {
            code = $@"
public string {field.Name}
{{
	get
	{{
		fixed ({structName}*@this = &this)
		{{
			var pointer = &@this->{field.BackingFieldName}[0];
            var cString = new CStringWide(pointer);
            return StringWide.ToString(cString);
		}}
	}}
}}
".Trim();
        }
        else
        {
            var fieldTypeName = field.TypeInfo.Name;
            var elementType = fieldTypeName[..^1];
            if (elementType.EndsWith('*'))
            {
                elementType = "nint";
            }

            code = $@"
public readonly Span<{elementType}> {field.Name}
{{
	get
	{{
		fixed ({structName}*@this = &this)
		{{
			var pointer = &@this->{field.BackingFieldName}[0];
			var span = new Span<{elementType}>(pointer, {field.TypeInfo.ArraySizeOf});
			return span;
		}}
	}}
}}
".Trim();
        }

        return context.ParseMemberCode<PropertyDeclarationSyntax>(code);
    }
}

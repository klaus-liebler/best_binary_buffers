using BestBinaryBuffers.Model;
using BestBinaryBuffers.Parsing;

namespace BestBinaryBuffers.Tests;

public class ParsingTests
{
	[Fact]
	public void ParsesFullFixtureIntoOneNamespace()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);

		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());

		var ns = Assert.Single(namespaces);
		Assert.Equal("testns", ns.Name);
		Assert.Single(ns.Enums);
		Assert.Equal(2, ns.Structs.Count);
		Assert.Equal(4, ns.Classes.Count); // DeviceInfo, LightCommand, TextCommand, Wrapper
		Assert.Equal(3, ns.Messages.Count);
	}

	[Fact]
	public void EnumValuesDefaultToAutoIncrementUnlessExplicit()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var color = namespaces.Single().Enums.Single(e => e.Name == "Color");

		Assert.Equal([("Red", 0L), ("Green", 5L), ("Blue", 6L)], color.Values.Select(v => (v.Name, v.Value)));
		Assert.Equal(1, color.Size); // byte underlying type
	}

	[Fact]
	public void RequestAndResponseMessagesGetImplicitRequestIdFirst()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var ns = namespaces.Single();

		var ping = ns.Messages.Single(m => m.Name == "RequestPing");
		Assert.Equal(MessageKind.Request, ping.Kind);
		var first = Assert.IsType<FixedField>(ping.Fields[0]);
		Assert.Equal("requestId", first.Name);

		var notify = ns.Messages.Single(m => m.Name == "NotifyThing");
		Assert.Equal(MessageKind.Event, notify.Kind);
		Assert.DoesNotContain(notify.Fields, f => f.Name == "requestId");
	}

	[Fact]
	public void MessageArrayFieldsResolveToExpectedKinds()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var status = namespaces.Single().Messages.Single(m => m.Name == "ResponseStatus");

		Assert.IsType<UniformPackedArrayField>(status.Fields.Single(f => f.Name == "waypoints"));
		Assert.IsType<UniformVariableArrayField>(status.Fields.Single(f => f.Name == "tags"));
		var commands = Assert.IsType<PolymorphicArrayField>(status.Fields.Single(f => f.Name == "commands"));
		Assert.Equal(["LightCommand", "TextCommand"], commands.Variants.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal));
	}

	[Fact]
	public void StructFieldWithBinaryCountBecomesRepeatedField()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var mac = namespaces.Single().Structs.Single(s => s.Name == "Mac");

		var repeated = Assert.IsType<RepeatedField>(Assert.Single(mac.Fields));
		Assert.Equal(6, repeated.Count);
		Assert.IsType<FixedField>(repeated.Element);
	}

	[Fact]
	public void TrailingPolymorphicFieldInClassResolves()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var wrapper = namespaces.Single().Classes.Single(c => c.Name == "Wrapper");

		Assert.Equal(2, wrapper.Fields.Count);
		var selected = Assert.IsType<PolymorphicField>(wrapper.Fields[1]);
		Assert.Equal(2, selected.Variants.Count);
	}

	[Fact]
	public void UnqualifiedReferenceAcrossFilesResolvesWithinSameNamespace()
	{
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace shared;

			[BinaryType]
			public struct Point { public int X; public int Y; }
			""");
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace shared;

			[BinaryMessage(MessageKind.Event)]
			public class NotifyMoved { public Point To; }
			""");

		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var msg = namespaces.Single().Messages.Single();
		var field = Assert.IsType<StructRefField>(msg.Fields.Single(f => f.Name == "to"));
		Assert.Equal("Point", field.Struct.Name);
	}

	[Fact]
	public void QualifiedReferenceCrossesNamespaces()
	{
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace geometry;

			[BinaryType]
			public struct Point { public int X; public int Y; }
			""");
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace tracking;

			[BinaryMessage(MessageKind.Event)]
			public class NotifyMoved { public geometry.Point To; }
			""");

		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var msg = namespaces.Single(n => n.Name == "tracking").Messages.Single();
		var field = Assert.IsType<StructRefField>(msg.Fields.Single(f => f.Name == "to"));
		Assert.Equal("geometry", field.Struct.Namespace);
	}

	[Fact]
	public void NullNamespaceDeclarationsGetIdZero()
	{
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;

			[BinaryType]
			public struct Point { public int X; public int Y; }
			""");

		var idMap = new IdMap();
		var namespaces = SchemaParser.Parse(schema.Files, idMap);
		var ns = Assert.Single(namespaces);
		Assert.Equal("", ns.Name);
		Assert.Equal(0, ns.Id);
	}

	[Fact]
	public void OverriddenNameAndNamespaceAreUsedInsteadOfCSharpIdentifier()
	{
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace internalns;

			[BinaryType(name: "Point", @namespace: "wire")]
			public struct LegacyPointName { public int X; public int Y; }
			""");

		var namespaces = SchemaParser.Parse(schema.Files, new IdMap());
		var ns = namespaces.Single(n => n.Name == "wire");
		Assert.Equal("Point", ns.Structs.Single().Name);
	}
}

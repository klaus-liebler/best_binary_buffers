namespace BestBinaryBuffers.Tests;

public class GeneratorTests
{
	[Fact]
	public void GeneratesCppAndTsWithoutThrowingForFullFixture()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);

		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("namespace WsProtocol", cpp);
		Assert.Contains("namespace testns", cpp);
		Assert.Contains("export enum MessageKind", ts);
		Assert.Contains("testns", ts);
	}

	[Fact]
	public void StringFieldIsNullTerminatedNotLengthPrefixed()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// DeviceInfo.name / NotifyThing.message are StringField -- encode must write a 0x00 terminator,
		// never a 4-byte length prefix (the old JSON-schema generator's wire format, deliberately dropped).
		Assert.Contains("dest[pos++] = 0;", cpp);
		Assert.DoesNotContain("Length >> 24", cpp); // old length-prefix encoding pattern must be gone
		Assert.Contains("decodeCString", ts);
	}

	[Fact]
	public void ArrayCountPrefixesAreTwoBytesNotFour()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, _) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// UniformPackedArrayField (waypoints) / UniformVariableArrayField (tags) / PolymorphicArrayField
		// (commands) counts must be read/written as uint16_t (2 bytes), not the old uint32_t (4 bytes).
		Assert.Contains("uint16_t waypointsCount_", cpp);
		Assert.Contains("uint16_t tagsCount_", cpp);
		Assert.Contains("uint16_t commandsCount_", cpp);
	}

	[Fact]
	public void PolymorphicArrayGeneratesAppendHelperPerVariant()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("AppendResponseStatusCommandsLightCommandElement", cpp);
		Assert.Contains("AppendResponseStatusCommandsTextCommandElement", cpp);
		Assert.Contains("LightCommand.CLASS_ID", ts);
		Assert.Contains("TextCommand.CLASS_ID", ts);
	}

	[Fact]
	public void RepeatedFieldEmbedsFixedSizeArrayWithoutWirePrefix()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		Assert.Contains("uint8_t bytes[6];", cpp);
		Assert.Contains("bytes: number[];", ts);
	}

	[Fact]
	public void SameIdMapProducesStableIdsAcrossRepeatedRuns()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var idMap = new IdMap();

		var (cpp1, _) = SchemaCompiler.Generate(schema.Files, idMap);
		var (cpp2, _) = SchemaCompiler.Generate(schema.Files, idMap);

		Assert.Equal(cpp1, cpp2);
	}
}

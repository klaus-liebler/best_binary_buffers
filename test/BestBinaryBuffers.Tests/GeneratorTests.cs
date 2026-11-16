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
	public void TsDecodeLocalsDoNotCollideWithFieldNames()
	{
		// A field named like a local of the generated decode() (its parameters view/offset, the Reader r)
		// became a second "const" of the same name: "The symbol 'offset' has already been declared"
		// (found with plc.UploadRequest.offset in the Factory Control Unit's schema).
		using var schema = new TestSchema();
		schema.AddFile("""
			using BestBinaryBuffers;
			namespace clash;
			[BinaryType] public struct Pos { public uint r; public uint offset; }
			[BinaryType] public class Item { public uint view; public uint r; }
			[BinaryMessage(MessageKind.Request)]
			public class Chunk { public uint offset; public uint view; public uint r; public Pos pos;
				[BinaryMaxItemCount(4)] public byte[] data = System.Array.Empty<byte>(); }
			""");
		var (_, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// every decode function: its parameters and its consts must be distinct names
		var functions = System.Text.RegularExpressions.Regex.Matches(ts,
			@"export function decode\w*\(([^)]*)\)[^{]*\{(.*?)\n\t*return \{", System.Text.RegularExpressions.RegexOptions.Singleline);
		Assert.True(functions.Count >= 2, "the message's and the struct's decode functions");
		foreach (System.Text.RegularExpressions.Match f in functions)
		{
			var locals = new HashSet<string>(f.Groups[1].Value.Split(',').Select(p => p.Split(':')[0].Trim()).Where(n => n.Length > 0));
			var consts = System.Text.RegularExpressions.Regex.Matches(f.Groups[2].Value, @"^\t*const (\$?\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
			foreach (System.Text.RegularExpressions.Match c in consts)
			{
				var head = f.Value.Split('\n')[0];
				Assert.True(locals.Add(c.Groups[1].Value), $"'{c.Groups[1].Value}' declared twice in {head}");
			}
		}
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
		Assert.Contains("readCString", ts);
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
	public void MessageEmitsConstexprMaxSizeDerivedFromSchemaBounds()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, _) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// ResponseStatus mixes every variable-length kind (Waypoints/Tags/Commands are
		// Uniform*/Polymorphic) -- MAX_SIZE must be a real constexpr, not the runtime-dependent
		// EncodedSize(). Exact arithmetic (header 8 + Timestamp 4 + Position 12 + Mood 1 + Address 6 +
		// Waypoints (2+8*12) + Tags (2+4*16) + Commands (2+4*(2+64))) = 461, recomputed here so the test
		// breaks loudly if the formula in CppGenerator.MaxSizeOf ever silently drifts from EncodeField's
		// actual wire layout.
		Assert.Contains("constexpr size_t ResponseStatus_MAX_SIZE = 461;", cpp);
		// NotifyThing has just one bounded string (MaxLength 127) plus the 8-byte header.
		Assert.Contains("constexpr size_t NotifyThing_MAX_SIZE = 136;", cpp);
	}

	[Fact]
	public void CorrelationIsRequiredForRequestsAndResponsesOptionalForEvents()
	{
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		// A response that forgets the correlation of its request must not compile: no default value.
		Assert.Contains("inline size_t Encode([[maybe_unused]] const Payload& payload, uint8_t* dest, size_t dest_size, uint16_t correlationId) {", cpp);
		Assert.Contains("inline size_t Encode([[maybe_unused]] const Payload& payload, uint8_t* dest, size_t dest_size, uint16_t correlationId = 0) {", cpp);
		Assert.Contains("export function encode(payload: Payload, $correlationId: number): Uint8Array {", ts);
		Assert.Contains("export function encode(payload: Payload, $correlationId = 0): Uint8Array {", ts);
		Assert.DoesNotContain("requestId", cpp);
		Assert.DoesNotContain("requestId", ts);
	}

	[Fact]
	public void GeneratedTsRoundTripsHeaderPayloadAndErrorReply()
	{
		// Runs the generated module with node (type stripping + transform for the namespaces): the wire
		// layout of the 8-byte header, the correlation, the payload and a generic error reply.
		using var schema = new TestSchema();
		schema.AddFile(Fixtures.FullSchema);
		var (_, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());
		var dir = Directory.CreateTempSubdirectory("bbb_ts_");
		try
		{
			File.WriteAllText(Path.Combine(dir.FullName, "ws-protocol.ts"), ts);
			File.WriteAllText(Path.Combine(dir.FullName, "check.ts"), """
				import { testns, readHeader, encodeError, decodeError, HEADER_SIZE, FLAG_ERROR, ErrorCode } from "./ws-protocol.ts";
				const fail = (what: string) => { console.log("FAIL " + what); process.exit(1); };
				const status = { timestamp: 7, position: { x: 1, y: 2, z: 3 }, mood: testns.Color.Blue, address: { bytes: [1, 2, 3, 4, 5, 6] },
					waypoints: [{ x: 4, y: 5, z: 6 }], tags: ["a", "bc"], commands: [{ classId: testns.LightCommand.CLASS_ID, brightness: 9 }] };
				const bytes = testns.ResponseStatus.encode(status, 0x1234);
				const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
				const h = readHeader(view);
				if (h.namespaceId !== testns.NAMESPACE_ID || h.messageTypeId !== testns.ResponseStatus.TYPE_ID) fail("ids");
				if (h.correlationId !== 0x1234 || h.flags !== 0) fail("correlation/flags");
				if (bytes[4] !== 0x34 || bytes[5] !== 0x12) fail("correlation little-endian at byte 4");
				if (view.getUint32(HEADER_SIZE, true) !== 7) fail("payload starts after the 8-byte header");
				const back = testns.ResponseStatus.decode(view, 0);
				if (JSON.stringify(back) !== JSON.stringify(status)) fail("payload round trip " + JSON.stringify(back));
				const ev = testns.NotifyThing.encode({ message: "hi" });
				if (readHeader(new DataView(ev.buffer)).correlationId !== 0) fail("event without correlation");
				const err = encodeError(h, ErrorCode.Busy, "later");
				const ev2 = new DataView(err.buffer);
				const eh = readHeader(ev2);
				if (eh.flags !== FLAG_ERROR || eh.correlationId !== 0x1234 || eh.messageTypeId !== h.messageTypeId) fail("error header");
				const e = decodeError(ev2);
				if (e.code !== ErrorCode.Busy || e.message !== "later") fail("error body");
				console.log("OK");
				""");
			var psi = new System.Diagnostics.ProcessStartInfo("node", "--experimental-transform-types --no-warnings check.ts")
			{
				WorkingDirectory = dir.FullName,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using var node = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("node not found (needed for this test)");
			var output = node.StandardOutput.ReadToEnd() + node.StandardError.ReadToEnd();
			node.WaitForExit();
			Assert.True(node.ExitCode == 0 && output.Trim() == "OK", output);
		}
		finally
		{
			dir.Delete(recursive: true);
		}
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

using System.Diagnostics;
using Xunit.Abstractions;

namespace BestBinaryBuffers.Tests;

// Echter Kodier-/Dekodier-Rundlauf statt reiner Textpruefung: generierter C++-Code wird kompiliert und
// ausgefuehrt, generierter TS-Code mit node ausgefuehrt. Beide Seiten kodieren dieselbe Nutzlast; die Bytes
// muessen identisch sein, und jede Seite dekodiert die Bytes der anderen.
//
// Werkzeuge: node (wie GeneratedTsRoundTripsHeaderPayloadAndErrorReply, Pflicht) und ein C++-Compiler ueber
// BBB_CXX (Default "g++" im PATH). Ist der Compiler nicht startbar, wird nur der C++-Teil uebersprungen
// (Hinweis im Testprotokoll) -- ein Kompilierfehler dagegen laesst den Test fehlschlagen.
public class RoundTripTests(ITestOutputHelper output)
{
	// Polymorphe Felder NICHT am Ende und mehrfach, dazu eine Klasse (Group) mit eigenem polymorphem Feld
	// mitten in ihren Feldern als Element einer polymorphen Liste -- genau die Faelle, die erst das
	// Byte-Laengenpraefix ermoeglicht (vorher: Group verschluckte beim Dekodieren alle Folgeelemente).
	// Pwm: eine Klasse mit allen drei Listenarten (feste Elementgroesse, Strings, polymorph) -- Listen in
	// Klassen sind seit 2026-11-17 erlaubt.
	private const string Schema = """
		using BestBinaryBuffers;
		namespace rt;

		[BinaryUnion] public interface IInner { }
		[BinaryType] public class Dot : IInner { public byte V; }
		[BinaryType] public class Word : IInner { [BinaryMaxEncodedByteLength(15)] public string W; }

		[BinaryUnion] public interface IShape { }
		[BinaryType] public class Circle : IShape { public ushort R; }
		[BinaryType] public class Group : IShape { public byte Tag; public IInner Inner; public ushort After; }
		[BinaryType] public class Pwm : IShape
		{
			[BinaryMaxItemCount(8)] public ushort[] Outputs;
			[BinaryMaxItemCount(4)] [BinaryMaxEncodedByteLength(7)] public string[] Tags;
			[BinaryMaxItemCount(4)] public IInner[] Parts;
			public byte End;
		}

		[BinaryMessage(MessageKind.Event)]
		public class Scene
		{
			public uint Head;
			[BinaryMaxItemCount(8)] public IShape[] First;
			public IInner Single;
			[BinaryMaxItemCount(8)] public IShape[] Second;
			public ushort Tail;
		}
		""";

	// Gemeinsame Testnutzlast (C++ und TS muessen exakt diese Werte kodieren/erwarten):
	//   head 0xA1B2C3D4
	//   first  [Circle(7), Group(9, Word("hi"), 0x1234), Circle(300)]
	//   single Dot(42)
	//   second [Group(1, Dot(5), 2), Pwm([16390, 7], ["a", "bc"], [Dot(3), Word("x")], 0x5A)]
	//   tail 0xBEEF
	private const string CppProgram = """
		#include <cstdio>
		#include <cstdlib>
		#include <cstring>
		#include <string>
		#include <type_traits>
		#include "ws_protocol.hh"
		using namespace WsProtocol;

		static int fails = 0;
		#define CHECK(c) do { if (!(c)) { std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

		static size_t encodeScene(uint8_t* out, size_t outSize) {
		    static uint8_t inner1[32], inner2[32], first[128], single[16], second[128];
		    static uint8_t outs[16], tags[16], parts[32];
		    size_t n1 = rt::AppendGroupInnerWordElement(rt::Word::Payload{"hi"}, inner1, 0, sizeof inner1);
		    size_t n2 = rt::AppendGroupInnerDotElement(rt::Dot::Payload{5}, inner2, 0, sizeof inner2);
		    size_t p = 0;
		    p = rt::AppendSceneFirstCircleElement(rt::Circle::Payload{7}, first, p, sizeof first);
		    p = rt::AppendSceneFirstGroupElement(rt::Group::Payload{9, inner1, n1, 0x1234}, first, p, sizeof first);
		    p = rt::AppendSceneFirstCircleElement(rt::Circle::Payload{300}, first, p, sizeof first);
		    size_t firstSize = p;
		    size_t singleSize = rt::AppendSceneSingleDotElement(rt::Dot::Payload{42}, single, 0, sizeof single);
		    size_t secondSize = rt::AppendSceneSecondGroupElement(rt::Group::Payload{1, inner2, n2, 2}, second, 0, sizeof second);
		    const uint16_t outValues[] = {16390, 7};
		    for (size_t i = 0; i < 2; i++)
		        rt::EncodePwmOutputsElement(outValues[i], outs + i * rt::PwmOutputs_ELEMENT_SIZE, sizeof outs - i * rt::PwmOutputs_ELEMENT_SIZE);
		    std::memcpy(tags, "a\0bc\0", 5);
		    size_t pp = rt::AppendPwmPartsDotElement(rt::Dot::Payload{3}, parts, 0, sizeof parts);
		    pp = rt::AppendPwmPartsWordElement(rt::Word::Payload{"x"}, parts, pp, sizeof parts);
		    rt::Pwm::Payload pwm{outs, 2, tags, 2, 5, parts, 2, pp, 0x5A};
		    secondSize = rt::AppendSceneSecondPwmElement(pwm, second, secondSize, sizeof second);
		    rt::Scene::Payload s{0xA1B2C3D4u, first, 3, firstSize, single, singleSize, second, 2, secondSize, 0xBEEF};
		    size_t len = rt::Scene::Encode(s, out, outSize);
		    CHECK(len == rt::Scene::EncodedSize(s));
		    CHECK(len <= rt::Scene::Scene_MAX_SIZE);
		    return len;
		}

		static void checkScene(const uint8_t* data, size_t len) {
		    rt::Scene::Payload s{};
		    CHECK(rt::Scene::Decode(data, len, s));
		    CHECK(s.head == 0xA1B2C3D4u);
		    CHECK(s.tail == 0xBEEF); // nur korrekt, wenn alle polymorphen Felder davor sauber uebersprungen wurden
		    CHECK(s.firstCount == 3);
		    CHECK(s.secondCount == 2);
		    std::string firstSeen;
		    CHECK(rt::DecodeSceneFirstElements(s.firstData, s.firstDataSize, s.firstCount, [&](const auto& item) {
		        using T = std::decay_t<decltype(item)>;
		        if constexpr (std::is_same_v<T, rt::Circle::Payload>) {
		            firstSeen += "C" + std::to_string(item.r);
		        } else if constexpr (std::is_same_v<T, rt::Pwm::Payload>) {
		            firstSeen += "P";
		        } else {
		            firstSeen += "G" + std::to_string(item.tag) + "/" + std::to_string(item.after);
		            CHECK(rt::DecodeGroupInnerElements(item.innerData, item.innerDataSize, 1, [&](const auto& in) {
		                using I = std::decay_t<decltype(in)>;
		                if constexpr (std::is_same_v<I, rt::Word::Payload>) firstSeen += std::string("W") + in.w;
		                else firstSeen += "D" + std::to_string(in.v);
		            }));
		        }
		    }));
		    CHECK(firstSeen == "C7G9/4660WhiC300");
		    std::string singleSeen;
		    CHECK(rt::DecodeSceneSingleElements(s.singleData, s.singleDataSize, 1, [&](const auto& item) {
		        using T = std::decay_t<decltype(item)>;
		        if constexpr (std::is_same_v<T, rt::Dot::Payload>) singleSeen += "D" + std::to_string(item.v);
		        else singleSeen += "W";
		    }));
		    CHECK(singleSeen == "D42");
		    std::string secondSeen;
		    CHECK(rt::DecodeSceneSecondElements(s.secondData, s.secondDataSize, s.secondCount, [&](const auto& item) {
		        using T = std::decay_t<decltype(item)>;
		        if constexpr (std::is_same_v<T, rt::Group::Payload>) {
		            secondSeen += "G" + std::to_string(item.tag) + "/" + std::to_string(item.after);
		            CHECK(rt::DecodeGroupInnerElements(item.innerData, item.innerDataSize, 1, [&](const auto& in) {
		                using I = std::decay_t<decltype(in)>;
		                if constexpr (std::is_same_v<I, rt::Dot::Payload>) secondSeen += "D" + std::to_string(in.v);
		                else secondSeen += "W";
		            }));
		        } else if constexpr (std::is_same_v<T, rt::Pwm::Payload>) {
		            secondSeen += "P";
		            for (size_t i = 0; i < item.outputsCount; i++) {
		                uint16_t v{};
		                CHECK(rt::DecodePwmOutputsElementAt(item.outputsData, i, v));
		                secondSeen += std::to_string(v) + ",";
		            }
		            secondSeen += "|";
		            CHECK(rt::DecodePwmTagsElements(item.tagsData, item.tagsDataSize, item.tagsCount, [&](const char* t) { secondSeen += std::string(t) + ","; }));
		            secondSeen += "|";
		            CHECK(rt::DecodePwmPartsElements(item.partsData, item.partsDataSize, item.partsCount, [&](const auto& in) {
		                using I = std::decay_t<decltype(in)>;
		                if constexpr (std::is_same_v<I, rt::Dot::Payload>) secondSeen += "D" + std::to_string(in.v);
		                else secondSeen += std::string("W") + in.w;
		            }));
		            secondSeen += "|" + std::to_string(item.end);
		        } else {
		            secondSeen += "C";
		        }
		    }));
		    CHECK(secondSeen == "G1/2D5P16390,7,|a,bc,|D3Wx|90");
		    // Laengenpraefix muss zum Inhalt passen: eine Liste mit zu grossem Byte-Bereich wird abgelehnt
		    CHECK(!rt::DecodeSceneFirstElements(s.firstData, s.firstDataSize + 1, s.firstCount, [](const auto&) {}));
		}

		int main(int argc, char** argv) {
		    uint8_t buf[512];
		    size_t len = encodeScene(buf, sizeof buf);
		    checkScene(buf, len);
		    // abgeschnittener Frame darf nicht als gueltig dekodiert werden
		    rt::Scene::Payload cut{};
		    CHECK(!rt::Scene::Decode(buf, len - 1, cut));
		    if (argc > 1) { // von TS kodierte Bytes (hex) dekodieren
		        const char* hex = argv[1];
		        size_t n = std::strlen(hex) / 2;
		        uint8_t other[512];
		        for (size_t i = 0; i < n; i++) { unsigned v; std::sscanf(hex + 2 * i, "%2x", &v); other[i] = (uint8_t)v; }
		        checkScene(other, n);
		    }
		    for (size_t i = 0; i < len; i++) std::printf("%02x", buf[i]);
		    std::printf("\n");
		    return fails == 0 ? 0 : 1;
		}
		""";

	private const string TsProgram = """
		import { rt } from "./ws-protocol.ts";

		const payload: rt.Scene.Payload = {
			head: 0xA1B2C3D4,
			first: [
				{ classId: rt.Circle.CLASS_ID, r: 7 },
				{ classId: rt.Group.CLASS_ID, tag: 9, inner: { classId: rt.Word.CLASS_ID, w: "hi" }, after: 0x1234 },
				{ classId: rt.Circle.CLASS_ID, r: 300 },
			],
			single: { classId: rt.Dot.CLASS_ID, v: 42 },
			second: [
				{ classId: rt.Group.CLASS_ID, tag: 1, inner: { classId: rt.Dot.CLASS_ID, v: 5 }, after: 2 },
				{ classId: rt.Pwm.CLASS_ID, outputs: [16390, 7], tags: ["a", "bc"],
				  parts: [{ classId: rt.Dot.CLASS_ID, v: 3 }, { classId: rt.Word.CLASS_ID, w: "x" }], end: 0x5A },
			],
			tail: 0xBEEF,
		};
		const hex = (b: Uint8Array) => Array.from(b, x => x.toString(16).padStart(2, "0")).join("");
		const fromHex = (h: string) => new Uint8Array(h.match(/../g)!.map(x => parseInt(x, 16)));

		const encoded = rt.Scene.encode(payload);
		const cppHex = process.argv[2];
		if (cppHex !== undefined) {
			if (hex(encoded) !== cppHex) throw new Error("TS-Bytes weichen ab:\n TS  " + hex(encoded) + "\n C++ " + cppHex);
			const bytes = fromHex(cppHex);
			const decoded = rt.Scene.decode(new DataView(bytes.buffer), 0);
			if (JSON.stringify(decoded) !== JSON.stringify(payload))
				throw new Error("TS dekodiert C++-Bytes falsch:\n" + JSON.stringify(decoded) + "\n" + JSON.stringify(payload));
			let rejected = false;
			try { rt.Scene.decode(new DataView(bytes.buffer, 0, bytes.length - 1), 0); } catch { rejected = true; }
			if (!rejected) throw new Error("abgeschnittener Frame wurde nicht abgelehnt");
		}
		console.log(hex(encoded));
		""";

	[Fact]
	public void CppAndTsAgreeOnWireFormatWithNonTrailingPolymorphicFields()
	{
		using var schema = new TestSchema();
		schema.AddFile(Schema);
		var (cpp, ts) = SchemaCompiler.Generate(schema.Files, new IdMap());

		var dir = Directory.CreateTempSubdirectory("bbb-roundtrip-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(dir, "ws_protocol.hh"), cpp);
			File.WriteAllText(Path.Combine(dir, "ws-protocol.ts"), ts);
			File.WriteAllText(Path.Combine(dir, "main.cc"), CppProgram);
			File.WriteAllText(Path.Combine(dir, "main.ts"), TsProgram);

			var cxx = Environment.GetEnvironmentVariable("BBB_CXX") ?? "g++";

			string? cppHex = null;
			var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "rt.exe" : "rt");
			var compiled = RunIfAvailable(cxx, ["-std=c++20", "-Wall", "-Werror", "-o", exe, "main.cc"], dir);
			if (compiled is null)
			{
				output.WriteLine($"C++-Teil uebersprungen: Compiler '{cxx}' nicht startbar (BBB_CXX setzen).");
			}
			else
			{
				Assert.True(compiled.Value.ok, "Generierter C++-Code bzw. Testprogramm kompiliert nicht:\n" + compiled.Value.output);
				Assert.True(Run(exe, [], dir, out var runOut), "C++-Rundlauf fehlgeschlagen:\n" + runOut);
				cppHex = runOut.Trim();
				output.WriteLine("C++: " + cppHex);
			}

			// TS mit node (Type-Stripping + Transform fuer die Namespaces), wie GeneratedTsRoundTrips...
			string[] tsArgs = cppHex is null
				? ["--experimental-transform-types", "--no-warnings", "main.ts"]
				: ["--experimental-transform-types", "--no-warnings", "main.ts", cppHex];
			Assert.True(Run("node", tsArgs, dir, out var tsOut), "TS-Rundlauf fehlgeschlagen:\n" + tsOut);
			var tsHex = tsOut.Trim();
			output.WriteLine("TS:  " + tsHex);
			if (cppHex is not null)
			{
				Assert.Equal(cppHex, tsHex);
				Assert.True(Run(exe, [tsHex], dir, out var crossOut), "C++ dekodiert TS-Bytes falsch:\n" + crossOut);
			}
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { /* Temp-Verzeichnis, egal */ }
		}
	}

	// null, wenn das Werkzeug nicht startbar ist (nicht installiert) -- sonst Erfolg und Ausgabe
	private static (bool ok, string output)? RunIfAvailable(string file, string[] args, string dir)
	{
		try
		{
			var ok = Run(file, args, dir, out var output);
			return (ok, output);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	private static bool Run(string file, string[] args, string dir, out string output)
	{
		var psi = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var a in args) psi.ArgumentList.Add(a);
		using var p = Process.Start(psi)!;
		var stdout = p.StandardOutput.ReadToEndAsync();
		var stderr = p.StandardError.ReadToEndAsync();
		p.WaitForExit();
		output = stdout.Result + stderr.Result;
		return p.ExitCode == 0;
	}
}

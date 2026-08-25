using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;

namespace ValvePak.Test
{
	internal sealed class MultiChunkWriteTest
	{
		private const int FractionSize = 1024 * 1024;

		private string TempDirectory = string.Empty;

		[Before(HookType.Test)]
		public void SetUp()
		{
			TempDirectory = Path.Combine(Path.GetTempPath(), "ValvePakTest_" + Path.GetRandomFileName());
			Directory.CreateDirectory(TempDirectory);
		}

		[After(HookType.Test)]
		public void TearDown()
		{
			Directory.Delete(TempDirectory, recursive: true);
		}

		[Test]
		public async Task WriteMultiChunkPackage()
		{
			var dirPath = TempPath("test_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 10; i++)
				{
					var entry = package.AddFile($"files/chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);

					// 400 byte files with a 1024 chunk size, so three files per chunk
					await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)(i / 3));
				}

				package.AddFile("in_dir.txt", Encoding.UTF8.GetBytes("this file is in the directory file"));

				// Files with no data must stay in the directory file
				var emptyEntry = package.AddFile("empty.bin", [], multiChunk: true);
				await Assert.That(emptyEntry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);

				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("test_000.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_001.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_002.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_003.vpk")).Length).IsEqualTo(400L);
				await Assert.That(File.Exists(TempPath("test_004.vpk"))).IsFalse();
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.IsDirVPK).IsTrue();
				await Assert.That(readBack.ArchiveMD5SectionSize).IsEqualTo(4u * ChunkHashFraction.SectionEntrySize);
				await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(4);
			}

			await AssertPackageVerifies(readBack);

			for (var i = 0; i < 10; i++)
			{
				var entry = await AssertEntryData(readBack, $"files/chunked_{i}.bin", 400, (byte)i);
				await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)(i / 3));
			}

			var dirEntry = readBack.FindEntry("in_dir.txt");
			await Assert.That(dirEntry).IsNotNull();
			await Assert.That(dirEntry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);
		}

		[Test]
		public async Task WritesChunkHashFractions()
		{
			var dirPath = TempPath("fractions_dir.vpk");
			var fileA = CreateTestData(2 * FractionSize, 1); // exactly two fractions
			var fileB = CreateTestData(300_000, 2);

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024 * 1024;
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Chunk 0 is an exact multiple of the 1 MiB fraction size, which produces a trailing zero sized fraction
			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(4);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.AccessPackFileHashes[0].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Length).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[0].HashType).IsEqualTo(EHashType.MD5);
				await Assert.That(readBack.AccessPackFileHashes[0].Checksum).IsEquivalentTo(MD5.HashData(fileA.AsSpan(0, FractionSize)), CollectionOrdering.Matching);

				await Assert.That(readBack.AccessPackFileHashes[1].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[1].Offset).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[1].Length).IsEqualTo((uint)FractionSize);

				await Assert.That(readBack.AccessPackFileHashes[2].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[2].Offset).IsEqualTo((uint)(2 * FractionSize));
				await Assert.That(readBack.AccessPackFileHashes[2].Length).IsZero();

				await Assert.That(readBack.AccessPackFileHashes[3].ArchiveIndex).IsEqualTo((ushort)1);
				await Assert.That(readBack.AccessPackFileHashes[3].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[3].Length).IsEqualTo(300_000u);
			}

			await AssertPackageVerifies(readBack);
		}

		[Test]
		public async Task HashesFractionSpanningMultipleFiles()
		{
			var dirPath = TempPath("spanning_dir.vpk");
			var fileA = CreateTestData(600_000, 1);
			var fileB = CreateTestData(800_000, 2);

			using (var package = new Package())
			{
				// Both files fit in one chunk, and the 1 MiB fraction boundary falls in the middle of the second file
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(2);

			var fraction0 = new byte[FractionSize];
			fileA.CopyTo(fraction0, 0);
			fileB.AsSpan(0, FractionSize - fileA.Length).CopyTo(fraction0.AsSpan(fileA.Length));

			using (Assert.Multiple())
			{
				await Assert.That(readBack.AccessPackFileHashes[0].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Length).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[0].Checksum).IsEquivalentTo(MD5.HashData(fraction0), CollectionOrdering.Matching);

				await Assert.That(readBack.AccessPackFileHashes[1].Offset).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[1].Length).IsEqualTo((uint)(fileA.Length + fileB.Length - FractionSize));
				await Assert.That(readBack.AccessPackFileHashes[1].Checksum).IsEquivalentTo(MD5.HashData(fileB.AsSpan(FractionSize - fileA.Length)), CollectionOrdering.Matching);
			}

			await AssertPackageVerifies(readBack);
		}

		[Test]
		public async Task WritesChunkDataInterleavedByTreeOrder()
		{
			var dirPath = TempPath("interleaved_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				// Chunk data is written in file tree order, which groups files by extension.
				// Alternating extensions makes the write order differ from the add order,
				// so consecutive writes alternate between chunk files.
				for (var i = 0; i < 3; i++)
				{
					var entryA = package.AddFile($"a{i}.txt", CreateTestData(600, (byte)(i * 2)), multiChunk: true);
					var entryB = package.AddFile($"b{i}.jpg", CreateTestData(600, (byte)((i * 2) + 1)), multiChunk: true);

					using (Assert.Multiple())
					{
						await Assert.That(entryA.ArchiveIndex).IsEqualTo((ushort)i);
						await Assert.That(entryB.ArchiveIndex).IsEqualTo((ushort)i);
					}
				}

				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);

			for (var i = 0; i < 3; i++)
			{
				var entryA = await AssertEntryData(readBack, $"a{i}.txt", 600, (byte)(i * 2));
				var entryB = await AssertEntryData(readBack, $"b{i}.jpg", 600, (byte)((i * 2) + 1));

				using (Assert.Multiple())
				{
					await Assert.That(entryA.ArchiveIndex).IsEqualTo((ushort)i);
					await Assert.That(entryB.ArchiveIndex).IsEqualTo((ushort)i);
				}
			}
		}

		[Test]
		public async Task RemoveFileRewritesChunksAndHashes()
		{
			var dirPath = TempPath("removed_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 9; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				using (Assert.Multiple())
				{
					// Remove a file from the middle of chunk 0, and all files of chunk 1
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_1.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_3.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_4.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_5.bin")!)).IsTrue();
				}

				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("removed_000.vpk")).Length).IsEqualTo(800L);
				await Assert.That(File.Exists(TempPath("removed_001.vpk"))).IsFalse();
				await Assert.That(new FileInfo(TempPath("removed_002.vpk")).Length).IsEqualTo(1200L);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Hashes must cover the rewritten chunk layout, not the layout at the time the files were added
			await AssertPackageVerifies(readBack);

			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(2);

			foreach (var i in new[] { 0, 2, 6, 7, 8 })
			{
				await AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}

			await Assert.That(readBack.FindEntry("chunked_1.bin")).IsNull();
		}

		[Test]
		public async Task AddFileThrowsWhenExceedingChunkLimit()
		{
			using var package = new Package();
			package.WriteChunkSize = 1;

			// With a chunk size of 1 every file starts a new chunk
			PackageEntry? entry = null;

			for (var i = 0; i < 0x7FFF; i++)
			{
				entry = package.AddFile($"{i}.bin", [1], multiChunk: true);
			}

			await Assert.That(entry!.ArchiveIndex).IsEqualTo((ushort)0x7FFE);

			await Assert.That(() => package.AddFile("one too many.bin", [1], multiChunk: true)).ThrowsExactly<InvalidOperationException>()
				.WithMessageContaining("maximum amount of chunk files", StringComparison.Ordinal);
		}

		[Test]
		public async Task WriteVersion1MultiChunkPackage()
		{
			var dirPath = TempPath("v1_dir.vpk");

			using (var package = new Package())
			{
				package.Version = 1;
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 5; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(File.Exists(TempPath("v1_000.vpk"))).IsTrue();
				await Assert.That(File.Exists(TempPath("v1_001.vpk"))).IsTrue();
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.Version).IsEqualTo(1u);
				await Assert.That(readBack.ArchiveMD5SectionSize).IsZero();
			}

			// Version 1 has no hashes to verify beyond the file checksums
			await Assert.That(() => readBack.VerifyFileChecksums()).ThrowsNothing();

			for (var i = 0; i < 5; i++)
			{
				await AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}
		}

		[Test]
		public async Task WriteToStreamThrowsWithChunkedFiles()
		{
			using var package = new Package();
			package.AddFile("chunked.bin", CreateTestData(400, 0), multiChunk: true);

			using var output = new MemoryStream();
			await Assert.That(() => package.Write(output)).ThrowsExactly<InvalidOperationException>()
				.WithMessageContaining("chunk files", StringComparison.Ordinal);
		}

		[Test]
		public async Task WriteChunkSizeValidation()
		{
			using var package = new Package();
			await Assert.That(package.WriteChunkSize).IsEqualTo(200 * 1024 * 1024);
			await Assert.That(() => package.WriteChunkSize = 0).ThrowsExactly<ArgumentOutOfRangeException>();
			await Assert.That(() => package.WriteChunkSize = -1).ThrowsExactly<ArgumentOutOfRangeException>();
		}

		private string TempPath(string fileName)
		{
			return Path.Combine(TempDirectory, fileName);
		}

		private static async Task AssertPackageVerifies(Package package)
		{
			using (Assert.Multiple())
			{
				await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
				await Assert.That(() => package.VerifyChunkHashes()).ThrowsNothing();
				await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();
			}
		}

		private static async Task<PackageEntry> AssertEntryData(Package package, string path, int length, byte seed)
		{
			var entry = package.FindEntry(path);
			await Assert.That(entry).IsNotNull();

			package.ReadEntry(entry, out var data);
			await Assert.That(data).IsEquivalentTo(CreateTestData(length, seed), CollectionOrdering.Matching);

			return entry;
		}

		private static byte[] CreateTestData(int length, byte seed)
		{
			var data = new byte[length];

			for (var i = 0; i < length; i++)
			{
				data[i] = (byte)(seed + (i * 37));
			}

			return data;
		}
	}
}

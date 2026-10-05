using Xunit;

namespace Tests.Support;

/// <summary>
/// The two test collections that run ALONE, and the reason the suite has them: some state in this process
/// is one slot for every test at once, and a test that changes it changes it for whatever runs beside it.
///
/// <para><b>What "alone" means here.</b> xunit starts a collection marked <c>DisableParallelization</c>
/// only after every parallel collection has finished, and runs such collections one after another. So a
/// class in one of these two sees no other test running at all: not <c>"world"</c>, not <c>"db"</c>, not
/// the classes that have no collection of their own. That is the property each half below rests on.</para>
///
/// <para><b><c>"log"</c></b>: the log and the console, and process configuration. A capture
/// (<see cref="LogLineSink"/>, <see cref="ConsoleTap"/>) already excludes other captures through its own
/// gate, but it still collects every line every other running test writes, and a test that closes the log
/// (<see cref="LogShutdownWindow"/>), changes what it admits, flips its wire dump or attaches its file sink
/// changes logging for everyone. Here there is nobody else. Before this, the shutdown facts sat in a
/// collection that ran beside every capture (upstream run 35895702560 attempt 1), and
/// <c>MobAiTickTests.OneMobThrowingDoesNotCostTheOthersTheirPackets</c> failed CI with another test's
/// "invalid --ports" line in its tap and its own line missing (upstream run 35473395191). The environment,
/// <c>ServerConfig</c> and the channel-port pair belong here for the same reason: they are read by every
/// test in the process.</para>
///
/// <para><b><c>"tile-translation"</c></b>: the content snapshot. A class here publishes a STUBBED content
/// table to the whole process (a one-row <c>AreaSpawns.csv</c>, a renamed header, an era date of 2008) and
/// asserts on what the loader made of it. Every test in the process reads that same snapshot, so a stub
/// published beside them is a wrong answer handed to a test that never asked for it:
/// <c>NagnangShieldQuestTests.GreenSquirrelsDropThePeltAndAreReachableFromNagnang</c> failed fork run
/// 35906110389 holding <c>ContentSmokeTests.AreaSpawnColumnsAreRequiredPerFile</c>'s one-row map-330 stub.
/// The name is historical: the collection began as the tile-translation tables' (#187, #284) and now holds
/// every content swapper.</para>
///
/// <para><b>Which seam goes where</b> is not a convention kept by hand. <see cref="TestSeamCollectionTests"/>
/// reads every class under <c>Tests/</c> and fails, naming the class, the seam and the line, when a class
/// touches a process-global seam from a collection that does not own it. The <c>"world"</c> collection is
/// the one parallel-phase collection allowed the captures and the static world hooks: it runs its classes
/// one at a time, and with every other capture and every log-state change in <c>"log"</c>, nothing else
/// in the parallel phase holds them.</para>
///
/// <para><b>The price</b> is that these two collections' time is added to the run's instead of overlapping
/// it. That is paid for by <see cref="StatusProbePrefixTests"/>, whose two handshake-watchdog facts spend
/// 15 s each and used to do it on the <c>"world"</c> collection's critical path while needing nothing from
/// it but a <c>World</c> of its own.</para>
/// </summary>
[CollectionDefinition("log", DisableParallelization = true)]
public sealed class LogCollection { }

/// <summary>See <see cref="LogCollection"/>: the content swappers' collection, run alone for the same
/// reason.</summary>
[CollectionDefinition("tile-translation", DisableParallelization = true)]
public sealed class TileTranslationCollection { }

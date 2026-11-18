"""Verify the solution layout, the docs folders and immutable finalized sample bytes."""
import hashlib
import re
import unittest
from pipeline_harness import PROJECT, FEEDS

BROKER_PACKAGES = ("solace", "solclient", "ibmmq", "ibm.wmq", "ibm.xms", "rabbitmq", "kafka", "amqp", "activemq",
                   "apache.nms", "azure.messaging", "awssdk.sqs", "awssdk.sns", "masstransit", "nservicebus")


class ProjectLayoutTests(unittest.TestCase):
    def test_docs_holds_only_samples_and_diagrams(self):
        docs = PROJECT / "docs"
        self.assertEqual({p.name for p in docs.iterdir() if p.is_dir()}, {"sample-docs", "diagrams"})
        for obsolete in ["input-feeds", "output-feeds", "failed-feeds", "feed-logs", "feed-processing", "input", "output",
                         "solace-feeds", "batch-runs", ".processing", ".feed-listener.lock"]:
            self.assertFalse((docs / obsolete).exists(), obsolete)

    def test_solution_separates_core_messaging_broker_and_worker(self):
        for project in ["src/PresidioDemo.Core", "src/PresidioDemo.Messaging", "src/PresidioDemo.Messaging.Solace",
                        "src/PresidioDemo.Worker", "tests/PresidioDemo.Messaging.Testing"]:
            folder = PROJECT / project
            self.assertTrue((folder / (folder.name + ".csproj")).is_file(), project)
        self.assertTrue((PROJECT / "src/PresidioDemo.Worker/appsettings.json").is_file())
        core = (PROJECT / "src/PresidioDemo.Core/PresidioDemo.Core.csproj").read_text(encoding="utf-8")
        self.assertNotIn("solclient", core.lower())
        self.assertNotIn("solace", core.lower())
        messaging = (PROJECT / "src/PresidioDemo.Messaging/PresidioDemo.Messaging.csproj").read_text(encoding="utf-8")
        packages = [name.lower() for name in re.findall(r'<PackageReference\s+Include="([^"]+)"', messaging)]
        for package in packages:
            self.assertFalse(any(broker in package for broker in BROKER_PACKAGES), package)
        self.assertNotIn("PresidioDemo.Messaging.Solace", messaging)

    def test_per_feed_parsers_and_policy_xml_are_removed(self):
        core = PROJECT / "src/PresidioDemo.Core"
        for area in ("Parser", "Serializer", "Decoder", "Models", "Reporting"):
            for feed in ("PNR", "Seats"):
                self.assertFalse((core / area / feed).exists(), f"{area}/{feed}")
        for name in ("feed-policies.xml", "xdr-layouts.xml", "reference-layouts.xml"):
            self.assertFalse((core / "Configuration" / name).exists(), name)
        for name in ("XdrFeedCodec.cs", "ReferenceLayoutFeedCodec.cs"):
            self.assertFalse((core / "Parser/Shared" / name).exists(), name)
        recognizers = {p.name for p in (core / "Configuration/recognizers").glob("*.xml")}
        self.assertEqual(recognizers, {"seats.xml"})

    def test_core_concern_folders_are_flat(self):
        # CR-002: one folder per concern holding a base and its per-feed children; no Parser, Serializer, feed or Shared folders.
        core = PROJECT / "src/PresidioDemo.Core"
        concerns = {p.name for p in core.iterdir() if p.is_dir() and p.name not in ("bin", "obj")}
        self.assertEqual(concerns, {"Configuration", "Decoder", "Logging", "Models", "Pipeline", "Protection"})
        for concern in ("Decoder", "Logging", "Models", "Pipeline"):
            self.assertEqual([p.name for p in (core / concern).iterdir() if p.is_dir()], [], concern)
        self.assertEqual([p.name for p in (core / "Protection").iterdir() if p.is_dir()], ["python"])

    def test_the_seats_feed_has_one_class_per_concern_over_its_base(self):
        core = PROJECT / "src/PresidioDemo.Core"
        expected = {
            "Decoder": {"SeatsParser": "JsonFeedParser(FeedType.Seats, JsonTokenType.StartObject)"},
            "Pipeline": {"SeatsWorkflow": "ProtectedFeedWorkflow(protector, codec)"},
            "Logging": {"SeatsFeedLogger": ": FeedLogger"},
        }
        for concern, classes in expected.items():
            for name, base in classes.items():
                text = (core / concern / (name + ".cs")).read_text(encoding="utf-8")
                self.assertIn(f"public sealed class {name}", text, name)
                self.assertIn(base, text, name)
        for base in ("Decoder/JsonFeedParser.cs", "Pipeline/ProtectedFeedWorkflow.cs", "Logging/FeedLogger.cs"):
            self.assertIn("public abstract class", (core / base).read_text(encoding="utf-8"), base)
        self.assertIn("Seats = 3", (core / "Models/FeedContracts.cs").read_text(encoding="utf-8"), "FeedType keeps Seats' value")
        protector = (core / "Protection/DetectingFeedProtector.cs").read_text(encoding="utf-8")
        for member in ("Changed = 0", "Kept = 1", "Allowed = 2", "NothingDetected = 3", "BelowThreshold = 4"):
            self.assertIn(member, protector, "DetectionDecision values are explicit (WR-014)")

    def test_each_concern_folder_holds_only_the_seats_feed_and_shared_files(self):
        # The Seats worker is Seats only, with the JSON parser and no XDR decoder: any other feed's class in these folders
        # fails this check.
        core = PROJECT / "src/PresidioDemo.Core"
        expected = {
            "Decoder": {"JsonFeedParser.cs", "SeatsParser.cs"},
            "Pipeline": {"ProtectedFeedWorkflow.cs", "SeatsWorkflow.cs", "FeedWorkflowRouter.cs", "FeedCodecRegistry.cs",
                         "FeedMessageProcessor.cs", "FeedPipelineConfiguration.cs"},
            "Logging": {"FeedLogger.cs", "SeatsFeedLogger.cs", "FeedLoggers.cs", "FeedTransactionLog.cs", "DetectionLog.cs"},
        }
        for concern, files in expected.items():
            self.assertEqual({p.name for p in (core / concern).glob("*.cs")}, files, concern)

    def test_namespaces_follow_project_and_folder(self):
        mismatches = []
        for path in sorted((PROJECT / "src").rglob("*.cs")):
            relative = path.relative_to(PROJECT / "src")
            if relative.parts[1] in ("bin", "obj"):
                continue
            match = re.search(r"^namespace ([A-Za-z.]+);", path.read_text(encoding="utf-8"), re.M)
            expected = ".".join(relative.parts[:-1])
            if match and match.group(1) != expected:
                mismatches.append(str(relative))
        self.assertEqual(mismatches, [])

    def test_test_folders_match_their_projects(self):
        for folder in (PROJECT / "tests").iterdir():
            projects = list(folder.glob("*.csproj")) if folder.is_dir() else []
            if projects:
                self.assertEqual([p.stem for p in projects], [folder.name], folder.name)
        self.assertFalse((PROJECT / "tests/DetectionSpike").exists())
        self.assertFalse((PROJECT / "tests/JsonFeeds").exists())
        self.assertFalse((PROJECT / "tests/JsonFeedChecks").exists())

    def test_samples_are_finalized_txt_files_in_one_folder_per_feed_with_unchanged_bytes(self):
        expected = {
            "Seats/SEATS.txt": "3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf",
            "Seats/SEATS_02_Negative_Testing.txt": "43b78c7ad1a318ab9a2aa259ed4e74c1ce8615fec9aa043079a53ed684794ca8",
            "Seats/SEATS_03.txt": "0b9bd0aa4d6be9fef748049b9c5133122e81b02fc3306cc07a2b41f149fd5575",
        }
        samples = PROJECT / "docs" / "sample-docs"
        # Samples are grouped in one folder per feed route, named like the exchange folders.
        self.assertEqual({p.name for p in samples.iterdir()}, set(FEEDS))
        self.assertTrue(all(p.is_dir() for p in samples.iterdir()))
        self.assertEqual({p.relative_to(samples).as_posix() for p in samples.rglob("*") if p.is_file()}, set(expected))
        for name, checksum in expected.items():
            self.assertTrue((samples / name).is_file())
            self.assertEqual(hashlib.sha256((samples / name).read_bytes()).hexdigest(), checksum)


if __name__ == "__main__":
    unittest.main()

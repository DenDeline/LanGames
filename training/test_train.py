"""Focused checks for the split and action contracts used by offline training."""

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from train import (CADENCE_TICKS, CLASS_TO_AXIS, FEATURE_NAMES, INPUT_NAME,
                   OUTPUT_NAME, check_split_isolation, classes_from_logits,
                   load_manifest, load_split)


class TrainingContractTests(unittest.TestCase):
    def test_ties_match_dotnet_stay_first_argmax(self):
        logits = np.asarray([
            [1.0, 1.0, 1.0],
            [2.0, 2.0, 1.0],
            [1.0, 2.0, 2.0],
            [2.0, 1.0, 2.0],
            [0.0, -1.0, 1.0],
        ], dtype=np.float32)
        self.assertEqual([1, 1, 1, 0, 2], classes_from_logits(logits).tolist())

    def test_manifest_rejects_seed_shared_by_train_and_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            manifest = self._manifest()
            manifest["splits"][1]["matchSeeds"] = ["train-seed"]
            (directory / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, "crosses split boundaries"):
                load_manifest(directory)

    def test_rows_cannot_cross_match_boundary_or_decision_cadence(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "manifest.json").write_text(json.dumps(self._manifest()))
            row = self._row("validation-0", CADENCE_TICKS)
            (directory / "train.jsonl").write_text(json.dumps(row) + "\n")
            with self.assertRaisesRegex(ValueError, "outside train matches"):
                load_split([directory], "train")
            row = self._row("train-0", CADENCE_TICKS + 1)
            (directory / "train.jsonl").write_text(json.dumps(row) + "\n")
            with self.assertRaisesRegex(ValueError, "decision cadence"):
                load_split([directory], "train")

    def test_independent_sources_cannot_reuse_train_seed_for_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            first = Path(temporary) / "first"
            second = Path(temporary) / "second"
            first.mkdir()
            second.mkdir()
            self._write_valid(first, self._manifest())
            other = self._manifest()
            other["splits"][1]["matchSeeds"] = ["train-seed"]
            other["splits"][0]["matchSeeds"] = ["another-train-seed"]
            self._write_valid(second, other)
            train = load_split([first], "train")
            validation = load_split([second], "validation")
            with self.assertRaisesRegex(ValueError, "Training/validation match seed overlap"):
                check_split_isolation(train, validation)

    def test_copied_source_cannot_repeat_train_or_validation_match_seed(self):
        with tempfile.TemporaryDirectory() as temporary:
            first = Path(temporary) / "first"
            copy = Path(temporary) / "copy"
            first.mkdir()
            copy.mkdir()
            self._write_valid(first, self._manifest())
            self._write_valid(copy, self._manifest())
            with self.assertRaisesRegex(ValueError, "training match seed is repeated"):
                check_split_isolation(load_split([first, copy], "train"),
                                      load_split([first], "validation"))
            with self.assertRaisesRegex(ValueError, "validation match seed is repeated"):
                check_split_isolation(load_split([first], "train"),
                                      load_split([first, copy], "validation"))

    @staticmethod
    def _row(match_id, tick):
        return {"matchId": match_id, "tick": tick,
                "observation": [0.0] * 10, "teacherClass": 1, "behaviorAxis": 0}

    @staticmethod
    def _manifest():
        return {
            "observationVersion": 1,
            "featureNames": FEATURE_NAMES,
            "inputName": INPUT_NAME,
            "outputName": OUTPUT_NAME,
            "classToAxis": CLASS_TO_AXIS,
            "inferenceCadenceTicks": CADENCE_TICKS,
            "masterSeed": "one",
            "behavior": "teacher",
            "splits": [
                {"name": name, "matchCount": 1, "rowCount": 1,
                 "matchIds": [f"{name}-0"], "matchSeeds": [f"{name}-seed"]}
                for name in ("train", "validation", "test")
            ],
        }

    @classmethod
    def _write_valid(cls, directory, manifest):
        (directory / "manifest.json").write_text(json.dumps(manifest))
        for name in ("train", "validation", "test"):
            (directory / f"{name}.jsonl").write_text(
                json.dumps(cls._row(f"{name}-0", CADENCE_TICKS)) + "\n")


if __name__ == "__main__":
    unittest.main()

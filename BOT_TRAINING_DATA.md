# Bot training data and evaluation (schema 1)

This document describes the offline data generator added in bot plan step 5. The
generator runs the same `GameEngine.Advance(GameConstants.FixedStepSeconds, ...)`
used by local and LAN play. Every trajectory starts with `StartMatch()` and changes
only through legal paddle axes. The teacher evaluates alternate right-paddle aims
by restoring exact production-engine checkpoints into a separate simulation.

## Right-bot observation contract

`RightBotObservationV1` encodes a pre-action `GameState` as ten `float32` values in
the fixed order below. Coordinates use the game's normalized arena. Positive Y
and a positive paddle axis move down. Scores are divided by the winning score
(7). The contact forecast reflects the current ball velocity across the top and
bottom contact planes. If the ball is not approaching the right paddle, the
forecast fields are `0.5` and `2`. The contact time is capped at two seconds; the
contact position is calculated from the uncapped time.

| Index | Name | Meaning |
| ---: | --- | --- |
| 0 | `left_y` | Current left paddle center Y |
| 1 | `right_y` | Current right paddle center Y |
| 2 | `ball_x` | Current ball center X |
| 3 | `ball_y` | Current ball center Y |
| 4 | `ball_vx` | Current horizontal ball velocity |
| 5 | `ball_vy` | Current vertical ball velocity |
| 6 | `left_score_normalized` | Left score / 7 |
| 7 | `right_score_normalized` | Right score / 7 |
| 8 | `right_contact_y` | Wall-reflected right contact Y |
| 9 | `right_contact_time` | Right contact time, clipped to [0, 2] seconds |

Class `0` means axis `-1` (up), class `1` means axis `0` (stay), and class
`2` means axis `+1` (down). The intended inference cadence is nine engine ticks
(150 ms), matching Simple's sampling interval. The model input and output names
are `observation` and `logits`. The vector excludes hidden simulation fields,
event history, opponent policy identity, seed, and future actions.

## Reproduction commands

Run the standalone .NET tool from the repository root. It does not start the
web server or require a browser:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- evaluate \
  --output .artifacts/bot-eval-100-20261010.json --seed 20261010 --matches 100
dotnet run --project tools/LanPong.TrainingData -c Release -- generate \
  --output .artifacts/bot-data-reproduction --seed 20261010 \
  --train 48 --validation 12 --test 12 --gate-matches 100
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate \
  --output .artifacts/bot-direct-fair-100-20261012.json \
  --seed 20261012 --matches 100
```

`evaluate` compares Teacher and Simple as right-paddle policies against paired
left-opponent scenarios. Generation reruns that held-out gate before opening any
label file. The gate requires at least 24 completed scenarios, at least 12
score-decisive pairs, positive aggregate score improvement, no fewer Teacher
match wins, and a Wilson 95% lower bound above 0.5 for Teacher's share of
score-decisive pairs. Match duration is supplemental and cannot pass the gate.
A match that does not finish within the configured `--max-ticks` is an error,
not a synthetic win or loss.

`direct-evaluate` runs Teacher against Simple in the same engine, with both
side assignments per seed. Both paddles receive the same seeded legal input
during the 120-tick opening prefix, then the two policies take over. The report
records completed and capped games separately, along with the Wilson interval
for Teacher's completed-game win rate. This direct control is separate from the
varied-opponent export gate. The generation command uses a fresh directory
because the tool refuses a nonempty destination. The verified original is in
`.artifacts/bot-data`; output directory names do not enter the generated files.

For Step 6 student-rollout relabeling, pass an ONNX policy using the same model
input and output contract. The student drives the right paddle; the offline
teacher labels the states the student actually visits:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- generate \
  --output .artifacts/bot-student-rollout-v1 --seed 20261013 \
  --train 48 --validation 12 --test 12 --gate-matches 100 \
  --behavior student --student-model path/to/student.onnx
```

The tool also accepts `--behavior teacher` (default) or `--behavior simple`,
`--sample-every` in fixed ticks (default 9; a multiple of the nine-tick inference
cadence), and `--max-ticks` (default 20,000).
The trained artifact and its evaluation are documented in
[BOT_MODEL.md](BOT_MODEL.md).

## Dataset provenance

The generator uses a documented SplitMix64 version 1 stream, with independent
derived seeds for train, validation, test, and held-out teacher evaluation.
Every row from one `StartMatch()` to `GameOver` stays in a single split. The
same seeded left-opponent scenario is replayed with Teacher and Simple for a
paired comparison. Left opponents are sampled trackers with different reaction
rates, lookahead horizons, aimed returns, and tick-keyed seeded jitter. Their
actions remain legal production-engine axes. The seed, policy, result, and
match length are recorded for every match. The right-side Simple pilot begins
tracking an incoming ball at X = 0.72, retaining its nine-tick observation
interval and 0.25-second lookahead. This late reaction was chosen on the
development seed before the final evaluation seeds were inspected.

Each split is a UTF-8 JSON Lines file (`train.jsonl`, `validation.jsonl`, or
`test.jsonl`). A row is a pre-action observation and teacher label at a Playing
decision tick:

```json
{"matchId":"train-00000","tick":99,"observation":[0.48583335,0.5,0.5275,0.5095,0.55,0.19,0,0,0.6517409,0.74863636],"teacherClass":2,"behaviorAxis":1}
```

`manifest.json` records the schema, class mapping, sampling cadence, seed
scheme, split seeds/IDs, behavior policy, and the student model SHA-256 when
used. `matches.json` contains per-match scores, right-win flag, ticks, goals,
paddle hits, wall bounces, rallies, and row count. `evaluation.json` contains
the paired Teacher/Simple outcomes and the gate result. These are
machine-readable inputs for training and later replay checks. Generation
writes into a unique staging directory and moves it into place only after all
matches finish; it refuses a nonempty destination without changing its files.

## Verified teacher result and dataset

Development seed `20261007` was used while tuning the pilot and is **not** the
final holdout. With policies frozen, previously uninspected seed `20261010`
gave 100 complete paired scenarios: Teacher won 100/100 matches, Simple won
57/100, Teacher had the better score in 92 pairs and tied 8, and its aggregate
score margin improved by 620 points. The paired-score Wilson 95% interval is
`[0.9599, 1]` among decisive pairs, so the export gate passed. The evaluation
ran in 1.12 seconds on the development host.

| Left policy (20 scenarios each) | Teacher wins | Simple wins | Paired point improvement |
| --- | ---: | ---: | ---: |
| Delayed tracker | 20 | 18 | +75 |
| Sampled tracker | 20 | 16 | +100 |
| Contact tracker | 20 | 7 | +161 |
| Late counterpunch | 20 | 0 | +231 |
| Limited range counter | 20 | 16 | +53 |

A separate direct control on previously uninspected seed `20261012` completed
all 200 side-swapped games: Teacher won 200, Simple won 0, and none reached the
20,000-tick cap. Teacher won all 100 games from each side. Its completed-game
Wilson 95% interval is `[0.9812, 1]`. The corrected direct protocol uses the
same opening axis on both paddles; earlier asymmetric-prefix diagnostics are
excluded from these results.

The generated dataset in `.artifacts/bot-data` contains 48 train matches
(24,845 rows), 12 validation matches (5,104 rows), and 12 test matches
(6,013 rows): 72 complete matches and 35,962 labeled rows in total. Its six
machine-readable files occupy 6,296,913 bytes. Two independent small
generation runs produced byte-identical files. The ONNX student path was
checked with a fixed 10-input, three-logit model that drove the right paddle
while fresh teacher decisions labeled the visited states.

| File | SHA-256 |
| --- | --- |
| `manifest.json` | `8d30dc327839773a0c8a3cfc378b852304136bb5b85216bec36bb159726444f4` |
| `matches.json` | `c8f9572e6548cdd6228501eb47fb9c3e905c412299e8e820c86f4513685c776f` |
| `evaluation.json` | `fb2724e092d06e0da6a8407e1e9e3e7d089f90edc2df7ac7ed702952327b107b` |
| `train.jsonl` | `d9acc978c9afec669a3664d715c4510c3332ef03181ddbb9f88e41d8803c1e81` |
| `validation.jsonl` | `2e0bb86f3793790990114926025998672bb9e5d4dd05ce3f24fadc81e3236cba` |
| `test.jsonl` | `fedf4259a05685372d402ce869cbc8dbe4c94a1e5e6d95f1813893a49170065c` |

These results validate the teacher and data pipeline. They do not establish
the Step 7 win-rate requirement for the trained ONNX policy, which must be
measured separately with the deployed .NET inference path.

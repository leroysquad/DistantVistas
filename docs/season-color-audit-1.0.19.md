# Season colour audit (1.0.19)

Code audit before knob changes. In-game A/B is Jack's full-quit verify matrix.

| Band | Pre-1.0.19 failure | Fix |
|---|---|---|
| Mid-autumn | `FrostSeasonMin` 0.20 allowed FlagFrost mid-ramp; bushes excluded from canopy GetColor path; `Contains("flower")` also matched `*-flowering` berrybush | Gate 0.75; `IsSeasonFoliage` for leaves+bush; flower exclude skips `flowering` |
| Late autumn frost | Side frost baked into RGB; thaw remesh only cleared UP | Store pure GetColor + FlagFrost; mesher walls+UP |
| Deep winter | Mostly OK if FlagFrost set | Mesher side+crown wash scaled by live winter |
| Early spring | Wall frost stayed until rebake | Remesh on FrostSeasonMin edge clears mesher wash |
| Stale months | PaintRev 5 | PaintRev 6 forces overlay once |

Pass = far LOD reads like near vanilla for that calendar band after full quit + rejoin.

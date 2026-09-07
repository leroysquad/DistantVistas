// cursor-canvas-title: Distant Vistas Project Scorecard

import {
  BarChart,
  Callout,
  Card,
  CardBody,
  CardHeader,
  Code,
  Divider,
  Grid,
  H1,
  H2,
  H3,
  Pill,
  Row,
  Stack,
  Stat,
  Table,
  Text,
} from "cursor/canvas";

const AS_OF = "2026-09-07";
const OVERALL = 68;
const VERDICT = "Strong R&D / Not release-ready";
const OPEN = { critical: 2, high: 5, medium: 6 };

const CATEGORY_SCORES = [
  { label: "Vision", score: 92 },
  { label: "Eng depth", score: 88 },
  { label: "Visual", score: 84 },
  { label: "Product discipline", score: 86 },
  { label: "Login bake", score: 58 },
  { label: "Runtime perf", score: 42 },
  { label: "Stability", score: 55 },
  { label: "Docs/process", score: 82 },
  { label: "Release readiness", score: 38 },
] as const;

const COMPARISON_CATEGORIES = [
  "Ambition",
  "Engine intrusion",
  "Visual identity",
  "Login-time work",
  "Ship polish",
  "Process",
] as const;

const COMPARISON_SERIES = [
  { name: "Content pack", data: [20, 8, 35, 5, 90, 75] },
  { name: "QoL", data: [35, 18, 45, 10, 78, 68] },
  { name: "LOD tweak", data: [55, 62, 58, 45, 62, 58] },
  { name: "Distant Vistas", data: [95, 88, 90, 22, 44, 82], tone: "info" as const },
];

const STRENGTHS = [
  "~26.3k C# LOC fork with deep render/LOD pipeline — real 3D far terrain, not a heightmap skin.",
  "Live seasonal color bake: overlay and walking share one month-aware palette; frost/leaves handled.",
  "Farseer coexistence: 4.5× view-distance multiplier + 750 graphics hold; region shader overlay without Harmony.",
  "Persistent per-world LOD cache, visited-land retention, translucent water, unlimited render distance cap.",
  "Strong process: scripted checks matrix, bench harness, design docs, review-gated path to Mods.",
];

const GAPS_CRITICAL = [
  "BAKE-THRU — login overlay progress stalls (~86/1680 historical); far mesh-wait and serial bake paths still dominate wall time.",
  "GETCOLOR — historically 4096 columns × stack depth per L0; main-thread scalar sampling remains the ceiling even after SIMD post-processing.",
];

const GAPS_HIGH = [
  "SEASON — month retint only on next join, not mid-session `/time` changes.",
  "SKY-GAP — horizon/sky-ring seams when Farseer SkyTint drifts high (keep ~0.4 in Ctrl+Shift+F).",
  "PAUSE — autopause/unpause and join timing interactions still need hardening for unattended runs.",
  "SHIM — 1.22.7 API surface requires compat shims; breakage risk on game updates.",
  "FALSE-DONE — overlay can report progress while scouts/mesh gates still block drawable output.",
];

const GAPS_MEDIUM = [
  "RAIN — wet-season / precipitation tint edge cases vs vanilla near field.",
  "4BOX — four-season splash alignment with spawn-solid radius and scout fill.",
  "PHOTO — screenshot/regression capture not wired into CI gates.",
  "GC — login bake still drives multi-GB managed growth in short sessions.",
  "DOCS-PUB — user-facing docs lag internal plans and branch-specific speed tips.",
  "TEST — playtest coverage strong on static checks; fewer end-to-end login-bake perf gates.",
];

const BENCHMARK_TAKEAWAYS = [
  "Login bake bottleneck shape: serial stop processing + far mesh-wait + GetColor/texture-mean — not raw scout count.",
  "1.0.30 baseline: ~86/1680 stops (~5%), often 1/16 scouts active early; ~7 GB GC in ~30 s sample.",
  "Post-GetColor SIMD (LodRgbSimd) is bit-identical and cheap vs GetColor — correct optimization tier.",
  "Parallel MaxBakePerTick=24 + ArrayPool scratch helps drain; still review-gated before Mods release.",
];

const PIPELINE_STEPS = [
  { step: "Speed", detail: "MaxBakePerTick 24, scout drain, ArrayPool scratch, two-tier mesh gate" },
  { step: "SIMD", detail: "LodRgbSimd post-GetColor BlurLand / Quantize / RGB pack (AVX2 → Vector128 → scalar)" },
  { step: "Expert", detail: "Farseer visit-mask debounce, partitioning every 8 ticks, spawn reveal throttle" },
  { step: "Review", detail: "Branch playtest on cursor/1.0.26-catchup-playtest-e27c before Mods gate" },
];

const PRODUCT_TRUTHS = [
  "Client-side mod: far land only where the server already sent chunk data; vanilla public servers skip overlay teleports.",
  "Server assist optional — matching client required when installed server-side.",
  "Review → Mods gate: shipping speed work (1.0.32) stays off main until playtest sign-off.",
  "High vision and visual scores do not offset login-time perf and release-readiness gaps — verdict stands.",
];

function scoreTone(score: number): "success" | "warning" | "danger" | undefined {
  if (score >= 75) return "success";
  if (score >= 55) return "warning";
  return "danger";
}

export default function DistantVistasMapCanvas() {
  return (
    <Stack gap={20}>
      <Row gap={12} align="center" wrap>
        <H1>Distant Vistas — Project Scorecard</H1>
        <Pill size="sm">as of {AS_OF}</Pill>
      </Row>

      <Callout tone="warning" title={VERDICT}>
        Composite score {OVERALL}/100 across nine categories. Vision and engineering depth are
        production-grade; login bake throughput and runtime perf block a Mods-ready release today.
      </Callout>

      <Grid columns={4} gap={12}>
        <Stat value={`${OVERALL}`} label="Overall /100" tone="warning" />
        <Stat value={OPEN.critical} label="Open critical" tone="danger" />
        <Stat value={OPEN.high} label="Open high" tone="warning" />
        <Stat value={OPEN.medium} label="Open medium" tone="info" />
      </Grid>

      <Card>
        <CardHeader trailing={<Pill size="sm">headline</Pill>}>Project snapshot</CardHeader>
        <CardBody>
          <Grid columns={2} gap={16}>
            <Stack gap={6}>
              <Text weight="semibold">Scale</Text>
              <Text tone="secondary" size="small">
                ~26.3k C# LOC · +36k vs Vintage Horizons · ~104 commits on active catch-up branch
              </Text>
            </Stack>
            <Stack gap={6}>
              <Text weight="semibold">Visual milestone</Text>
              <Text tone="secondary" size="small">
                Liked look at 1.0.23 — seasonal overlay, far terrain identity holding
              </Text>
            </Stack>
            <Stack gap={6}>
              <Text weight="semibold">Farseer stack</Text>
              <Text tone="secondary" size="small">
                4.5× view-distance multiplier + 750 graphics hold; region shader overlay (no Harmony)
              </Text>
            </Stack>
            <Stack gap={6}>
              <Text weight="semibold">Login bake symptom</Text>
              <Text tone="secondary" size="small">
                Historical ~86/1680 stops (~5%) in sample sessions — progress UI outruns drawable mesh
              </Text>
            </Stack>
          </Grid>
          <Divider />
          <Text tone="secondary" size="small">
            Gate: <Code>Review → Mods</Code> — speed and SIMD work lands on{" "}
            <Code>cursor/1.0.26-catchup-playtest-e27c</Code> (1.0.32) before public Mods release.
          </Text>
        </CardBody>
      </Card>

      <H2>Category scores</H2>
      <Text tone="secondary" size="small">
        Source: repo audit · {AS_OF} · scores normalized /100
      </Text>
      <Grid columns={3} gap={12}>
        {CATEGORY_SCORES.map((cat) => (
          <Stat
            key={cat.label}
            value={cat.score}
            label={cat.label}
            tone={scoreTone(cat.score)}
          />
        ))}
      </Grid>

      <H2>Mod comparison</H2>
      <Text tone="secondary" size="small">
        Relative scores /100 · axes: ambition vs polish vs login cost · Distant Vistas vs typical mod archetypes
      </Text>
      <BarChart
        categories={[...COMPARISON_CATEGORIES]}
        series={COMPARISON_SERIES}
        horizontal
        height={320}
        yMin={0}
        yMax={100}
        valueSuffix=""
        showValues
      />

      <Grid columns={2} gap={16}>
        <Stack gap={12}>
          <H2>Strengths</H2>
          {STRENGTHS.map((item) => (
            <Text key={item} tone="secondary" size="small">
              {item}
            </Text>
          ))}
        </Stack>

        <Stack gap={12}>
          <H2>Gaps</H2>
          <H3>Critical</H3>
          <Table
            headers={["ID", "Summary"]}
            rows={GAPS_CRITICAL.map((g) => {
              const [id, ...rest] = g.split(" — ");
              return [id, rest.join(" — ")];
            })}
            rowTone={["danger", "danger"]}
            framed={false}
          />
          <H3>High</H3>
          <Table
            headers={["ID", "Summary"]}
            rows={GAPS_HIGH.map((g) => {
              const [id, ...rest] = g.split(" — ");
              return [id, rest.join(" — ")];
            })}
            rowTone={GAPS_HIGH.map(() => "warning" as const)}
            framed={false}
          />
          <H3>Medium</H3>
          <Table
            headers={["ID", "Summary"]}
            rows={GAPS_MEDIUM.map((g) => {
              const [id, ...rest] = g.split(" — ");
              return [id, rest.join(" — ")];
            })}
            rowTone={GAPS_MEDIUM.map(() => "info" as const)}
            framed={false}
          />
        </Stack>
      </Grid>

      <H2>Benchmark takeaways</H2>
      <Card variant="borderless">
        <CardBody>
          <Stack gap={8}>
            {BENCHMARK_TAKEAWAYS.map((item) => (
              <Text key={item} tone="secondary" size="small">
                {item}
              </Text>
            ))}
          </Stack>
        </CardBody>
      </Card>

      <H2>Paths / pipeline</H2>
      <Callout tone="info" title="Speed tip — 1.0.32 on catch-up branch">
        Latest speed work is on <Code>cursor/1.0.26-catchup-playtest-e27c</Code> as version 1.0.32:
        SIMD <Code>LodRgbSimd</Code> post-GetColor plus bake parallelization (MaxBakePerTick 24, scout
        drain, ArrayPool scratch). Still <Code>Review → Mods</Code> gated before public Mods release.
      </Callout>
      <Table
        headers={["Stage", "Focus"]}
        rows={PIPELINE_STEPS.map((p) => [p.step, p.detail])}
        columnAlign={["left", "left"]}
        striped
      />

      <H2>Product truths</H2>
      <Stack gap={8}>
        {PRODUCT_TRUTHS.map((item) => (
          <Text key={item} tone="secondary" size="small">
            {item}
          </Text>
        ))}
      </Stack>

      <Divider />

      <Callout tone="neutral" title="Open this canvas">
        Press <Code>Ctrl+Shift+P</Code> → <Code>View: Open Canvas</Code> → choose{" "}
        <Code>distant-vistas-map</Code>. Do not double-click the <Code>.tsx</Code> file in the tree.
      </Callout>
    </Stack>
  );
}

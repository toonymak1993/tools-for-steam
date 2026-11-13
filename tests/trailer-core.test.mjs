import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/trailer-core.js");

// Steam's real dash_h264.mpd for a store trailer (shortened to the parts the player reads).
const steamManifest = `<?xml version='1.0' encoding='utf-8'?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" profiles="urn:mpeg:dash:profile:isoff-live:2011" type="static" mediaPresentationDuration="PT1M25.5S" maxSegmentDuration="PT3.0S" minBufferTime="PT5.9S">
  <Period id="0" start="PT0.0S">
    <AdaptationSet id="0" contentType="video" startWithSAP="1" segmentAlignment="true" bitstreamSwitching="true" frameRate="60000/1001" maxWidth="1920" maxHeight="1080" par="16:9">
      <Representation id="0" mimeType="video/mp4" codecs="avc1.640029" bandwidth="5800000" width="1920" height="1080" sar="1:1">
        <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1">
        </SegmentTemplate>
      </Representation>
      <Representation id="1" mimeType="video/mp4" codecs="avc1.640029" bandwidth="2600000" width="1280" height="720" sar="1:1">
        <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1">
        </SegmentTemplate>
      </Representation>
      <Representation id="3" mimeType="video/mp4" codecs="avc1.640029" bandwidth="1000000" width="640" height="360" sar="1:1">
        <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1">
        </SegmentTemplate>
      </Representation>
    </AdaptationSet>
    <AdaptationSet id="1" contentType="audio" startWithSAP="1" segmentAlignment="true" bitstreamSwitching="true" lang="eng">
      <Representation id="4" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="192000" audioSamplingRate="48000">
        <AudioChannelConfiguration schemeIdUri="urn:mpeg:dash:23003:3:audio_channel_configuration:2011" value="2" />
        <SegmentTemplate timescale="1000000" duration="3000000" initialization="dash_h264/init-stream$RepresentationID$.m4s" media="dash_h264/chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1">
        </SegmentTemplate>
      </Representation>
    </AdaptationSet>
    <AdaptationSet id="2" mimeType="image/avif" contentType="image">
      <SegmentTemplate media="$RepresentationID$/thumbnails-$Number%05d$.avif" duration="90" startNumber="1" />
      <Representation id="dash_thumbnails" bandwidth="0" width="1280" height="864" />
    </AdaptationSet>
  </Period>
</MPD>`;

function loadInternals() {
  const window = { __tfsTrailerTestHooks: true };
  const context = vm.createContext({ window, URL, console });
  const result = vm.runInContext(source, context, { filename: "trailer-core.js" });
  assert.equal(result, "test");
  return window.__tfsTrailerInternals;
}

test("parses Steam's DASH trailer manifest", () => {
  const { parseMpd, parseIsoDuration } = loadInternals();
  assert.equal(parseIsoDuration("PT1M25.5S"), 85.5);
  assert.equal(parseIsoDuration("PT2H"), 7200);
  assert.equal(parseIsoDuration("nonsense"), 0);

  const manifest = parseMpd(steamManifest);
  assert.equal(manifest.duration, 85.5);
  assert.deepEqual(Array.from(manifest.video, (item) => item.height), [1080, 720, 360]);
  assert.equal(manifest.audio.length, 1);
  assert.equal(manifest.audio[0].codecs, "mp4a.40.2");
  assert.equal(manifest.video[0].template.timescale, 1000000);
  assert.equal(manifest.video[0].template.startNumber, 1);
  assert.equal(parseMpd("<MPD type=\"dynamic\" mediaPresentationDuration=\"PT10S\"></MPD>"), null);
  assert.equal(parseMpd("<html></html>"), null);
});

test("builds segment URLs and counts like Steam's numbering", () => {
  const { parseMpd, expandTemplate, segmentCount, segmentIndexAt, resolveUrl } = loadInternals();
  const manifest = parseMpd(steamManifest);
  const video = manifest.video[1];
  const base = "https://video.akamai.steamstatic.com/store_trailers/1145360/312686/abc/1750627549/dash_h264.mpd?t=1600353465";

  assert.equal(
    resolveUrl(base, expandTemplate(video.template.media, video, 7)),
    "https://video.akamai.steamstatic.com/store_trailers/1145360/312686/abc/1750627549/dash_h264/chunk-stream1-00007.m4s");
  assert.equal(
    resolveUrl(base, expandTemplate(video.template.initialization, video, 0)),
    "https://video.akamai.steamstatic.com/store_trailers/1145360/312686/abc/1750627549/dash_h264/init-stream1.m4s");
  assert.equal(segmentCount(video, 85.5), 29);
  assert.equal(segmentCount(video, 84), 28);
  assert.equal(segmentIndexAt(video, 0), 0);
  assert.equal(segmentIndexAt(video, 8.9), 2);
});

test("picks the best playable stream for the screen", () => {
  const { parseMpd, pickVideo, pickAudio } = loadInternals();
  const manifest = parseMpd(steamManifest);
  const all = () => true;
  assert.equal(pickVideo(manifest.video, 1080, all).height, 1080);
  assert.equal(pickVideo(manifest.video, 720, all).height, 720);
  assert.equal(pickVideo(manifest.video, 480, all).height, 360);
  assert.equal(pickVideo(manifest.video, 200, all).height, 360, "falls back to the smallest stream");
  assert.equal(pickVideo(manifest.video, 1080, () => false), null);
  assert.equal(pickAudio(manifest.audio, all).id, "4");
  assert.equal(pickAudio(manifest.audio, () => false), null);
});

test("plays only on the real game details page", () => {
  const { appIdFromPath, editorAppIdFromPath } = loadInternals();
  assert.equal(appIdFromPath("/library/app/1145360"), 1145360);
  assert.equal(appIdFromPath("/library/app/1145360/"), 1145360);
  assert.equal(appIdFromPath("/library/app/1145360/achievements/my"), 0);
  assert.equal(appIdFromPath("/library/home"), 0);
  assert.equal(appIdFromPath("/library/tab/AllGames"), 0);
  assert.equal(appIdFromPath("/library/app/99999999999"), 0);
  assert.equal(editorAppIdFromPath("/tfs/trailers/440"), 440);
  assert.equal(editorAppIdFromPath("/tfs/trailers"), 0);
  assert.equal(editorAppIdFromPath("/tfs/curtain/440"), -1);
});

test("trims keep at least a short clip", () => {
  const { playWindow } = loadInternals();
  assert.deepEqual({ ...playWindow(85.5, 5, 10) }, { start: 5, end: 75.5 });
  assert.deepEqual({ ...playWindow(85.5, 0, 0) }, { start: 0, end: 85.5 });
  assert.deepEqual({ ...playWindow(10, 6, 3) }, { start: 0, end: 10 }, "trims that leave less than 3 s are ignored");
});

test("runs only in the shared context and is wired into TFS", () => {
  assert.match(source, /if \(role && role !== "shared"\) return "skipped";/);
  assert.match(source, /Inspired by TrailerHero for Playhub \(MIT, LoZazaMastro\); independent code\./);
  assert.match(source, /"\/tfs\/trailers\/:appId", "\/tfs\/trailers"/);
  assert.doesNotMatch(source, /youtube\.com|googlevideo/i, "only Steam's own trailers are used");
  // X arrives as Big Picture's focus-navigation event; SteamClient's raw
  // controller messages carry action ids, not gamepad buttons.
  assert.match(source, /doc\.addEventListener\("vgp_onbuttondown", onGamepadButton, true\)/);
  assert.match(source, /const gamepadButtonSecondary = 3;/);
  assert.doesNotMatch(source, /RegisterForControllerInputMessages/);

  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  assert.match(host, /Assets\/curtain-core\.js"\),\s*\/\/ TFS Trailers[^\n]*\n\s*EmbeddedAssetReader\.ReadText\("Assets\/trailer-core\.js"\)/);
  assert.match(host, /Trailers = new Infrastructure\.Trailers\.TrailerService\(/);

  const server = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  assert.match(server, /\["\/api\/trailers"\] = "trailers"/);
  assert.match(server, /TrailerApiEndpoints\.TryHandleAsync\(request, response, Trailers, cancellationToken\)/);
});

test("the game context menu opens the per-game trailer page without duplicates", () => {
  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");
  assert.match(artwork, /key: "tfs-trailer-settings"/);
  assert.match(artwork, /items\[index\]\?\.key === "tfs-trailer-settings" \|\|/);
  assert.match(artwork, /window\.__tfsTrailers \|\| window\.opener\?\.__tfsTrailers/);
  assert.match(artwork, /getTrailerRuntime\(\)\?\.openEditor\?\.\(appId\)/);
});

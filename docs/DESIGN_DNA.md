# EasyFlix Design DNA

This document captures the visual and interaction choices that made the accepted EasyFlix 1.1 build feel polished. It is intentionally reusable across future Marcelo projects.

The goal is **not** to copy EasyFlix screen-for-screen. Reuse the design language, motion discipline, hierarchy, and polish principles while adapting each product to its own job.

## 1. Core feeling

EasyFlix should feel:

- premium but restrained;
- dark, cinematic, and calm;
- fast and deliberate rather than flashy;
- technically capable without looking like a developer tool;
- local-first and trustworthy;
- visually coherent from Desktop icon -> startup -> shell -> focused content.

The product should never feel like a default WPF window with styling added afterward.

## 2. Palette

Canonical EasyFlix reference colors:

- app background: `#05060A`
- near-black tile / splash mark: `#080A10`
- dark surface: `#0B0E14` to `#111722`
- elevated surface: `#151A23`
- subtle borders: `#263143` / `#344154`
- primary violet: `#7B6CFF`
- supporting violet: `#5B55C8`
- cyan accent: `#35D3FF`
- primary text: `#F8FAFF`
- secondary text: `#C7D0DD`
- muted text: `#8E9AAD`

Use bright color sparingly. Most of the UI should remain near-black / charcoal so the accent colors carry meaning.

## 3. Icon and logo language

The accepted Desktop icon fixed a key lesson: subtle transparent artwork can look invisible on a dark Windows Desktop even when technical alpha checks pass.

Rules:

- use a strong opaque backing shape;
- guarantee a readable silhouette at 16-32 px;
- prefer one dominant mark over fine detail;
- combine near-black / deep violet with a bright white glyph;
- use cyan as a small accent, not the dominant fill;
- judge the icon visually on an actual Desktop, not only by pixel metrics;
- the app icon, splash mark, and in-product branding should clearly belong to the same family.

EasyFlix 1.1 accepted icon reference:

- preserve the classic pre-R4H EasyFlix artwork as the recognizable product mark;
- place that mark on a fully opaque near-black backing so Windows cannot make it disappear at small sizes;
- use a restrained violet frame/accent rather than replacing the classic mark with a different symbol;
- generate the Windows ICO through the proven native HICON/System.Drawing path;
- require clear human readability on the real Desktop after technical alpha/contrast checks pass.

Future products should keep the same **clarity + contrast strategy** while using a mark appropriate to that product.

## 4. Startup experience

A desktop app should paint something intentional immediately.

Accepted pattern:

1. window appears on a dark background immediately;
2. centered branded mark is visible before expensive initialization;
3. product name and one short line may appear below it;
4. loading indication is subtle and honest;
5. the startup layer fades away when the real shell is ready.

EasyFlix reference copy:

- `EasyFlix`
- `Your library, local first.`

Rules:

- never show a blank white startup client;
- never fake progress percentages;
- do not delay startup just to display branding;
- let the first frame render before synchronous initialization can block the UI thread;
- prefer a short fade out around 200-260 ms.

## 5. Motion language

Motion should explain state changes, not decorate them.

Reference timings:

- chrome / sidebar / hero transitions: about **220 ms**
- focused-content transitions: about **260 ms**
- startup fade: about **240 ms**

Preferred properties:

- `Opacity`
- `TranslateTransform`
- small `ScaleTransform` changes when appropriate

Avoid:

- large continuous Width / Height animations;
- per-frame scroll-position polling;
- animations that force expensive full-layout work;
- bounce / elastic motion;
- long cinematic delays before content becomes usable.

Use ease-out motion for entering/stabilizing states. Keep navigation responsive while the animation runs.

## 6. Layering and hierarchy

EasyFlix works because it uses clear depth rather than many visual effects.

Recommended hierarchy:

- near-black application canvas;
- slightly lighter functional surfaces;
- quiet 1 px borders;
- modest corner radii;
- stronger accent only for selected / primary actions;
- dim/translucent backdrop for focused content;
- constrained centered detail panels instead of full-screen takeovers when the user should retain context.

Focused overlays should feel attached to the current app, not like a second application opening on top.

## 7. Navigation behavior

The accepted shell behavior establishes several reusable principles:

- persistent navigation may collapse when content needs room;
- collapse/reveal should be smooth and spatially understandable;
- preserve user context when opening focused content;
- Back should restore the prior browsing position/focus when practical;
- Home should have an obvious deterministic path;
- keyboard focus and hit testing must remain correct through transitions.

Animation must never make navigation ambiguous.

## 8. Content-first responsiveness

When the user scrolls into the library, chrome should get out of the way.

Reference EasyFlix behavior:

- sidebar / hero can collapse as the user moves down;
- content receives more visual area;
- re-expand at the true top rather than bouncing in and out;
- preserve scrollbar stability;
- avoid horizontal movement / sideways bounce.

This principle can carry into future data-heavy apps: **controls dominate only while they are useful**.

## 9. Desktop polish

A finished Windows app should feel installed, not copied out of a build folder.

Rules:

- one canonical Desktop launcher;
- installed executable lives in a stable app location;
- Desktop shortcut points to that installed executable;
- no duplicate `.exe` and `.lnk` entries;
- shortcut and executable use the same recognizable branding;
- product version should be exact and intentional;
- startup, icon, window chrome, and release artifact should agree on the product identity.

## 10. Performance discipline

Visual polish must not compromise responsiveness.

Rules:

- prefer compositor-friendly transforms and opacity;
- keep expensive catalog / filesystem work away from first paint when possible;
- show cached/local content first;
- defer reconciliation / maintenance work to idle/background paths;
- do not use network work as a normal startup dependency;
- validate on real 4K layouts when the target environment uses them.

## 11. Product personality

Good future Marcelo apps should share a family resemblance:

- dark, premium, technical;
- small violet/cyan highlights;
- high-contrast custom icon;
- branded first paint;
- clean typography;
- subtle borders;
- focused overlays / sheets;
- restrained 200-300 ms motion;
- no default-framework visual residue;
- little or no visual clutter.

But each product should still have its own symbol, information hierarchy, and personality.

Think **same studio, different product**.

## 12. Human acceptance rule

Automated UI metrics are necessary but not sufficient.

The EasyFlix icon passed earlier technical visibility checks and was still visually poor on the real Desktop.

For important visual elements always require a final human check for:

- small icon readability;
- dark/light wallpaper contrast;
- startup first frame;
- animation feel;
- spacing / hierarchy;
- obvious duplicate launchers;
- visual regressions that tests cannot measure.

## 13. Future-agent instruction

When designing or polishing another Marcelo project:

1. read this document as a **design-language reference**;
2. reuse the interaction principles and polish standards;
3. adapt the icon/symbol and layout to the new product;
4. preserve performance and accessibility;
5. do not copy EasyFlix-specific media UI into unrelated apps;
6. prefer a coherent cross-project studio identity over identical interfaces.

The target is a portfolio where different apps visibly belong to the same creator.

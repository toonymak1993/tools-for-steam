const moduleData = {
  audio: {
    tag: "Audio controls",
    badge: "Controller ready",
    title: "Audio",
    description: "Switch the Windows default output device and handle system volume without dropping out of the Big Picture flow.",
    points: [
      "Change default output without reaching for the desktop",
      "Keep simple volume actions close to the controller",
      "Useful for TVs, headsets, receivers, and docked setups"
    ],
    footnote: "A good fit for rooms where one PC regularly moves between more than one audio route."
  },
  processes: {
    tag: "Window handoff",
    badge: "Live state",
    title: "Processes",
    description: "See visible app windows in real time and push the selected one back to the front when a session loses focus.",
    points: [
      "List visible windows that matter during play",
      "Bring the right app forward from the controller",
      "Useful for launchers, emulators, overlays, and setup tools"
    ],
    footnote: "Helps most when the couch PC is juggling several supporting apps around one game session."
  },
  "store-sync": {
    tag: "Library bridge",
    badge: "Steam-facing",
    title: "Store Sync",
    description: "Scan supported launchers and custom folders so non-Steam games can be folded back into one Steam-centric library.",
    points: [
      "Combine launcher sources with custom paths",
      "Update Steam shortcuts with less manual work",
      "Optionally fetch artwork through SteamGridDB during sync"
    ],
    footnote: "Especially useful when Steam is meant to stay the single front door for the whole setup."
  },
  themes: {
    tag: "Visual layer",
    badge: "Profiles included",
    title: "Themes",
    description: "Manage bundled themes, saved profiles, and the early CSS-based groundwork used to tune supported Steam surfaces.",
    points: [
      "Enable and adjust installed themes",
      "Save and re-apply complete visual profiles",
      "Keep UI experiments organized instead of one-off"
    ],
    footnote: "This is where the project gets more personal without turning into a full custom shell."
  },
  display: {
    tag: "Output routing",
    badge: "Living room use",
    title: "Display",
    description: "Switch between internal and external display modes without walking back through Windows settings menus.",
    points: [
      "Useful for TV and monitor handoff",
      "Cuts down on keyboard-and-mouse interruptions",
      "Designed for docked and living room PC routines"
    ],
    footnote: "A small module with a big quality-of-life payoff for shared screens and hybrid desk setups."
  },
  performance: {
    tag: "Overlay tuning",
    badge: "Readability focused",
    title: "Performance",
    description: "Adjust Steam's FPS overlay and related readability settings so status data works better at TV distance.",
    points: [
      "Tune position, detail level, contrast, and scale",
      "Make overlay data more useful from the couch",
      "Keep adjustments close during test and play sessions"
    ],
    footnote: "Useful when the built-in overlay exists, but its defaults are not comfortable from the room."
  },
  hltb: {
    tag: "Game page context",
    badge: "Optional surface",
    title: "HLTB",
    description: "Show HowLongToBeat estimates on supported game pages to make backlog decisions faster inside Steam.",
    points: [
      "Surface playtime estimates where browsing already happens",
      "Choose the exact HLTB stats you want to show",
      "Keep decision-making inside the same interface"
    ],
    footnote: "Best for people using Big Picture as a browsing and selection surface, not only a launcher."
  },
  power: {
    tag: "Recovery actions",
    badge: "Safety net",
    title: "Power",
    description: "Restart Steam, recover the Windows desktop, or trigger power actions when a controller-first session gets stuck.",
    points: [
      "Controlled Steam restart with bridge support",
      "Desktop recovery without panic-alt-tabbing",
      "Collect system-level escape hatches in one place"
    ],
    footnote: "This module matters most when the setup is in another room and quick recovery really counts."
  },
  settings: {
    tag: "Global behavior",
    badge: "Base defaults",
    title: "Settings",
    description: "Handle startup preferences and general Tools for Steam behavior so the rest of the project stays predictable day to day.",
    points: [
      "Set practical defaults in one place",
      "Make new or rebuilt systems quicker to configure",
      "Keep the project coherent as more modules land"
    ],
    footnote: "The quiet module that keeps the rest of the utility usable over time."
  }
};

const tabs = [...document.querySelectorAll(".module-tab")];
const navToggle = document.querySelector(".nav-toggle");
const nav = document.querySelector(".site-nav");
const footerYear = document.getElementById("footer-year");
const workflowSection = document.getElementById("workflow");
const heroDetailPanel = document.getElementById("hero-detail-panel");
const detailStage = document.getElementById("detail-stage");
const detailRoute = document.getElementById("detail-route");
const detailLabel = document.getElementById("detail-label");
const detailTitle = document.getElementById("detail-title");
const detailVisual = document.getElementById("detail-visual");
const detailMeta1Label = document.getElementById("detail-meta1-label");
const detailMeta1Value = document.getElementById("detail-meta1-value");
const detailMeta2Label = document.getElementById("detail-meta2-label");
const detailMeta2Value = document.getElementById("detail-meta2-value");
const detailMeta3Label = document.getElementById("detail-meta3-label");
const detailMeta3Value = document.getElementById("detail-meta3-value");

const heroSurfaceData = [
  {
    route: "Quick Access > Audio",
    label: "Default output",
    title: "Living Room Speakers",
    visualClass: "detail-visual detail-visual-audio",
    visualHtml: `
      <div class="level-meter">
        <span></span>
        <span></span>
        <span></span>
        <span></span>
        <span></span>
        <span></span>
      </div>
    `,
    meta: [
      ["Windows", "7 ready"],
      ["Theme", "Gameview"],
      ["Sync", "Idle"]
    ],
    hints: ["A Select", "B Back", "Y Refresh"]
  },
  {
    route: "Quick Access > Store Sync",
    label: "Launcher scan",
    title: "3 sources connected",
    visualClass: "detail-visual detail-visual-sync",
    visualHtml: `
      <div class="detail-sync-core">
        <span class="detail-sync-dot"></span>
      </div>
    `,
    meta: [
      ["Shortcuts", "42 synced"],
      ["Artwork", "6 queued"],
      ["Last scan", "2m ago"]
    ],
    hints: ["A Sync now", "X Sources", "B Back"]
  },
  {
    route: "Quick Access > Processes",
    label: "Foreground handoff",
    title: "Launchers and tools visible",
    visualClass: "detail-visual detail-visual-processes",
    visualHtml: `
      <div class="detail-process-grid">
        <span class="detail-process-card"></span>
        <span class="detail-process-card"></span>
        <span class="detail-process-card"></span>
        <span class="detail-process-card"></span>
      </div>
    `,
    meta: [
      ["Windows", "7 ready"],
      ["Target", "EA App"],
      ["Overlay", "Attached"]
    ],
    hints: ["A Bring forward", "X Refresh", "B Back"]
  },
  {
    route: "Quick Access > Themes",
    label: "Active profile",
    title: "Clean Gameview",
    visualClass: "detail-visual detail-visual-themes",
    visualHtml: `
      <div class="detail-theme-stack">
        <span class="detail-theme-layer"></span>
        <span class="detail-theme-layer"></span>
        <span class="detail-theme-layer"></span>
      </div>
    `,
    meta: [
      ["Presets", "4 ready"],
      ["Accent", "Steam blue"],
      ["Layout", "Compact"]
    ],
    hints: ["A Apply", "Y Preview", "B Back"]
  },
  {
    route: "Quick Access > Power",
    label: "Recovery actions",
    title: "Desktop fallback ready",
    visualClass: "detail-visual detail-visual-power",
    visualHtml: `
      <div class="detail-power-core"></div>
    `,
    meta: [
      ["Steam", "Restart ready"],
      ["Desktop", "One press"],
      ["Recovery", "0 today"]
    ],
    hints: ["A Recover", "X Restart", "B Back"]
  }
];

if (workflowSection) {
  workflowSection.classList.add("workflow-sequenced");
}

function updateModule(tool) {
  const data = moduleData[tool];
  if (!data) {
    return;
  }

  document.getElementById("module-tag").textContent = data.tag;
  document.getElementById("module-badge").textContent = data.badge;
  document.getElementById("module-title").textContent = data.title;
  document.getElementById("module-description").textContent = data.description;
  document.getElementById("module-footnote").textContent = data.footnote;

  const list = document.getElementById("module-points");
  list.innerHTML = "";

  data.points.forEach((point) => {
    const item = document.createElement("li");
    item.textContent = point;
    list.appendChild(item);
  });

  tabs.forEach((tab) => {
    const selected = tab.dataset.tool === tool;
    tab.classList.toggle("is-selected", selected);
    tab.setAttribute("aria-pressed", String(selected));
  });
}

tabs.forEach((tab) => {
  tab.addEventListener("click", () => updateModule(tab.dataset.tool));
});

if (navToggle && nav) {
  navToggle.addEventListener("click", () => {
    const isOpen = nav.classList.toggle("is-open");
    navToggle.setAttribute("aria-expanded", String(isOpen));
  });

  nav.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", () => {
      nav.classList.remove("is-open");
      navToggle.setAttribute("aria-expanded", "false");
    });
  });
}

const revealItems = document.querySelectorAll(".reveal");
let workflowSequenceTimer = null;
let workflowSequenceStarted = false;
let heroSurfaceTimer = null;
let currentHeroSurfaceIndex = 0;
let heroSurfaceSequenceIndex = 0;
const heroSurfaceSequence = [0, 1, 0, 2, 0, 3, 0, 4];
const heroSurfaceDurations = {
  0: 6400,
  1: 3200,
  2: 3200,
  3: 3200,
  4: 3200
};

function setWorkflowCurrent(cards, rail, index) {
  cards.forEach((card, cardIndex) => {
    card.classList.toggle("is-current", cardIndex === index);
  });

  if (rail && cards.length > 1) {
    const progress = `${12 + (index / (cards.length - 1)) * 76}%`;
    rail.style.setProperty("--flow-progress", progress);
  }
}

function startWorkflowSequence() {
  if (!workflowSection || workflowSequenceStarted) {
    return;
  }

  workflowSequenceStarted = true;

  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const eyebrow = workflowSection.querySelector(".section-heading .eyebrow");
  const title = workflowSection.querySelector(".section-heading h2");
  const intro = workflowSection.querySelector(".section-heading p");
  const rail = workflowSection.querySelector(".flow-rail");
  const callout = workflowSection.querySelector(".flow-download-callout");
  const cards = [...workflowSection.querySelectorAll(".flow-step-card")];

  if (reduceMotion) {
    [title, eyebrow, intro, rail, callout].forEach((element) => {
      element?.classList.add("sequence-visible");
    });

    cards.forEach((card) => card.classList.add("sequence-visible"));
    setWorkflowCurrent(cards, rail, cards.length - 1);
    return;
  }

  window.setTimeout(() => title?.classList.add("sequence-visible"), 80);
  window.setTimeout(() => eyebrow?.classList.add("sequence-visible"), 280);
  window.setTimeout(() => intro?.classList.add("sequence-visible"), 440);
  window.setTimeout(() => rail?.classList.add("sequence-visible"), 620);

  cards.forEach((card, index) => {
    window.setTimeout(() => {
      card.classList.add("sequence-visible");
      setWorkflowCurrent(cards, rail, index);
    }, 860 + index * 320);
  });

  window.setTimeout(() => {
    callout?.classList.add("sequence-visible");
  }, 860 + cards.length * 320 + 120);

  window.setTimeout(() => {
    let currentIndex = cards.length - 1;
    workflowSequenceTimer = window.setInterval(() => {
      currentIndex = (currentIndex + 1) % cards.length;
      setWorkflowCurrent(cards, rail, currentIndex);
    }, 1650);
  }, 860 + cards.length * 320 + 560);
}

function renderHeroSurface(index) {
  const data = heroSurfaceData[index];
  if (!data || !detailRoute || !detailLabel || !detailTitle || !detailVisual) {
    return;
  }

  detailRoute.textContent = data.route;
  detailLabel.textContent = data.label;
  detailTitle.textContent = data.title;
  detailVisual.className = data.visualClass;
  detailVisual.innerHTML = data.visualHtml;

  const [meta1, meta2, meta3] = data.meta;
  if (meta1) {
    detailMeta1Label.textContent = meta1[0];
    detailMeta1Value.textContent = meta1[1];
  }
  if (meta2) {
    detailMeta2Label.textContent = meta2[0];
    detailMeta2Value.textContent = meta2[1];
  }
  if (meta3) {
    detailMeta3Label.textContent = meta3[0];
    detailMeta3Value.textContent = meta3[1];
  }

}

function advanceHeroSurface() {
  if (!heroDetailPanel || !detailStage) {
    return;
  }

  heroDetailPanel.classList.add("is-swapping");

  window.setTimeout(() => {
    heroSurfaceSequenceIndex = (heroSurfaceSequenceIndex + 1) % heroSurfaceSequence.length;
    currentHeroSurfaceIndex = heroSurfaceSequence[heroSurfaceSequenceIndex];
    renderHeroSurface(currentHeroSurfaceIndex);
    heroDetailPanel.classList.remove("is-swapping");
    heroDetailPanel.classList.add("is-entering");

    window.setTimeout(() => {
      heroDetailPanel.classList.remove("is-entering");
    }, 520);

    if (heroSurfaceTimer) {
      window.clearTimeout(heroSurfaceTimer);
    }

    heroSurfaceTimer = window.setTimeout(
      advanceHeroSurface,
      heroSurfaceDurations[currentHeroSurfaceIndex] ?? 3200
    );
  }, 250);
}

function startHeroSurfaceCycle() {
  if (!heroDetailPanel) {
    return;
  }

  renderHeroSurface(currentHeroSurfaceIndex);

  if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    return;
  }

  heroSurfaceSequenceIndex = 0;
  heroSurfaceTimer = window.setTimeout(advanceHeroSurface, heroSurfaceDurations[0]);
}

if ("IntersectionObserver" in window) {
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add("is-visible");
          if (entry.target.id === "workflow") {
            startWorkflowSequence();
          }
          observer.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.12 }
  );

  revealItems.forEach((item) => observer.observe(item));
} else {
  revealItems.forEach((item) => item.classList.add("is-visible"));
  startWorkflowSequence();
}

updateModule("audio");
startHeroSurfaceCycle();

if (footerYear) {
  footerYear.textContent = `Local preview ${new Date().getFullYear()} - Tools for Steam / TFS`;
}

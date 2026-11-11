// Chart.js helpers that follow the design system: colors come from CSS custom properties
// (so light/dark both work), thin marks, recessive grid, crosshair + tooltip on hover, no entrance animation.
/* global Chart */

export const cssVar = (name, el = document.documentElement) => getComputedStyle(el).getPropertyValue(name).trim();

/** Color for a transaction category, read from the same .cat-* classes the transaction rows use. */
export function categoryColor(category) {
  const probe = document.createElement("span");
  probe.className = `cat-${category}`;
  probe.style.display = "none";
  document.body.append(probe);
  const c = getComputedStyle(probe).getPropertyValue("--c").trim();
  probe.remove();
  return c || cssVar("--ink-3");
}

const usd = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const usdShort = (v) => (Math.abs(v) >= 1000 ? `$${(v / 1000).toFixed(Math.abs(v) >= 10000 ? 0 : 1)}k` : `$${Math.round(v)}`);
export const fmt = { usd: (v) => usd.format(v), short: usdShort };

/** Draws a vertical hairline at the hovered x position (crosshair) for line charts. */
const crosshair = {
  id: "crosshair",
  afterDatasetsDraw(chart) {
    const active = chart.tooltip?.getActiveElements?.();
    if (!active?.length) return;
    const { ctx, chartArea } = chart;
    const x = active[0].element.x;
    ctx.save();
    ctx.strokeStyle = cssVar("--line-strong");
    ctx.lineWidth = 1;
    ctx.setLineDash([3, 3]);
    ctx.beginPath();
    ctx.moveTo(x, chartArea.top);
    ctx.lineTo(x, chartArea.bottom);
    ctx.stroke();
    ctx.restore();
  }
};

export function baseOptions() {
  const ink3 = cssVar("--ink-3");
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false, // charts appear in place; nothing animates on load
    interaction: { mode: "index", intersect: false },
    layout: { padding: { top: 6 } },
    font: { family: "Manrope" },
    plugins: {
      legend: { display: false },
      tooltip: {
        backgroundColor: cssVar("--ink"),
        titleColor: cssVar("--bg"),
        bodyColor: cssVar("--bg"),
        padding: 10,
        cornerRadius: 6,
        displayColors: true,
        boxWidth: 8, boxHeight: 8, boxPadding: 4, usePointStyle: true,
        titleFont: { family: "Manrope", weight: "700" },
        bodyFont: { family: "Manrope" }
      }
    },
    scales: {
      x: { grid: { display: false }, border: { display: false }, ticks: { color: ink3, font: { family: "Manrope", size: 11 }, maxRotation: 0, autoSkipPadding: 18 } },
      y: {
        grid: { color: cssVar("--line"), drawTicks: false }, border: { display: false },
        ticks: { color: ink3, font: { family: "Manrope", size: 11 }, padding: 8, maxTicksLimit: 5, callback: usdShort }
      }
    }
  };
}

export function lineChart(canvas, labels, values, { label = "Balance", tooltipTitles } = {}) {
  const primary = cssVar("--primary");
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createLinearGradient(0, 0, 0, canvas.clientHeight || 280);
  gradient.addColorStop(0, primary + "38");
  gradient.addColorStop(1, primary + "00");
  const options = baseOptions();
  options.plugins.tooltip.callbacks = {
    title: (items) => (tooltipTitles ? tooltipTitles[items[0].dataIndex] : items[0].label),
    label: (item) => ` ${label}: ${usd.format(item.parsed.y)}`
  };
  options.plugins.tooltip.displayColors = false;
  options.scales.y.grace = "8%";
  return new Chart(canvas, {
    type: "line",
    data: {
      labels,
      datasets: [{
        label, data: values, borderColor: primary, borderWidth: 2, tension: 0.32, fill: true, backgroundColor: gradient,
        pointRadius: 0, pointHoverRadius: 5, pointHoverBorderWidth: 2, pointHoverBorderColor: cssVar("--surface"), pointHoverBackgroundColor: primary
      }]
    },
    options,
    plugins: [crosshair]
  });
}

export function donutChart(canvas, labels, values, colors) {
  const options = baseOptions();
  delete options.scales;
  options.interaction = { mode: "nearest", intersect: true };
  options.cutout = "72%";
  options.plugins.tooltip.callbacks = { label: (item) => ` ${item.label}: ${usd.format(item.parsed)}` };
  return new Chart(canvas, {
    type: "doughnut",
    data: { labels, datasets: [{ data: values, backgroundColor: colors, borderColor: cssVar("--surface"), borderWidth: 2, borderRadius: 4, hoverOffset: 6 }] },
    options
  });
}

export function barChart(canvas, labels, series) {
  const options = baseOptions();
  options.plugins.legend = {
    display: true, position: "top", align: "end",
    labels: { color: cssVar("--ink-2"), usePointStyle: true, pointStyle: "rectRounded", boxWidth: 8, boxHeight: 8, font: { family: "Manrope", weight: "600", size: 12 } }
  };
  options.plugins.tooltip.callbacks = { label: (item) => ` ${item.dataset.label}: ${usd.format(item.parsed.y)}` };
  return new Chart(canvas, {
    type: "bar",
    data: {
      labels,
      datasets: series.map((s) => ({
        label: s.label, data: s.values, backgroundColor: s.color, hoverBackgroundColor: s.color,
        borderRadius: { topLeft: 4, topRight: 4 }, borderSkipped: "bottom",
        // 2px surface gap between adjacent bars
        borderColor: cssVar("--surface"), borderWidth: { left: 1, right: 1 },
        categoryPercentage: 0.62, barPercentage: 0.92, maxBarThickness: 22
      }))
    },
    options
  });
}

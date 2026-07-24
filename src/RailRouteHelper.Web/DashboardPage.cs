namespace RailRouteHelper.Web;

internal static class DashboardPage
{
    public const string Html =
        """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>Rail Route Helper · Live Operations</title>
          <style>
            :root {
              color-scheme: dark;
              --bg: #08110f;
              --panel: #101d19;
              --panel-2: #152720;
              --line: #294139;
              --text: #edf7f1;
              --muted: #9db4a8;
              --mint: #7ce0ad;
              --amber: #ffc66d;
              --red: #ff817a;
              --blue: #78b7ff;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              min-height: 100vh;
              background:
                radial-gradient(circle at 8% -10%, #1d513c 0, transparent 31rem),
                linear-gradient(150deg, var(--bg), #0c1618 65%, #10141a);
              color: var(--text);
              font: 15px/1.5 Inter, ui-sans-serif, system-ui, sans-serif;
            }
            header, main { width: min(1280px, calc(100% - 32px)); margin: auto; }
            header {
              display: flex;
              align-items: end;
              justify-content: space-between;
              gap: 24px;
              padding: 36px 0 24px;
            }
            h1, h2, h3, p { margin: 0; }
            h1 { font-size: clamp(25px, 4vw, 42px); letter-spacing: -.04em; }
            h1 small {
              display: block;
              margin-bottom: 4px;
              color: var(--mint);
              font: 700 12px/1.2 ui-monospace, monospace;
              letter-spacing: .16em;
              text-transform: uppercase;
            }
            h2 { margin-bottom: 12px; font-size: 17px; }
            .connection { display: flex; align-items: center; gap: 8px; color: var(--muted); }
            .dot {
              width: 9px; height: 9px; border-radius: 50%;
              background: var(--amber); box-shadow: 0 0 16px currentColor;
            }
            .dot.live { color: var(--mint); background: var(--mint); }
            .dot.error { color: var(--red); background: var(--red); }
            .metrics {
              display: grid;
              grid-template-columns: repeat(4, minmax(0, 1fr));
              gap: 12px;
              margin-bottom: 24px;
            }
            .metric, .panel, .network {
              border: 1px solid var(--line);
              border-radius: 14px;
              background: color-mix(in srgb, var(--panel) 92%, transparent);
              box-shadow: 0 16px 50px #0004;
            }
            .metric { padding: 16px; }
            .metric span { color: var(--muted); font-size: 12px; text-transform: uppercase; }
            .metric strong { display: block; margin-top: 4px; font-size: 25px; }
            .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
            .panel { padding: 18px; margin-bottom: 16px; overflow: hidden; }
            .empty { padding: 20px 0; color: var(--muted); }
            .alert {
              display: grid;
              grid-template-columns: auto 1fr auto;
              gap: 12px;
              align-items: start;
              padding: 12px 0;
              border-top: 1px solid var(--line);
            }
            .alert:first-of-type { border-top: 0; }
            .badge {
              display: inline-flex; padding: 3px 8px; border-radius: 99px;
              background: #ffc66d1c; color: var(--amber);
              font: 700 11px/1.4 ui-monospace, monospace;
            }
            .badge.resolved { color: var(--mint); background: #7ce0ad1c; }
            .subtle { color: var(--muted); font-size: 12px; }
            .network { margin-bottom: 16px; overflow: hidden; }
            .network-head {
              display: flex; justify-content: space-between; gap: 16px;
              padding: 16px 18px; background: var(--panel-2);
            }
            .network-body { padding: 4px 18px 16px; overflow-x: auto; }
            table { width: 100%; border-collapse: collapse; white-space: nowrap; }
            th, td { padding: 11px 10px; border-bottom: 1px solid var(--line); text-align: left; }
            th { color: var(--muted); font-size: 11px; text-transform: uppercase; }
            td:first-child { color: var(--mint); font-weight: 700; }
            .route-list { display: grid; gap: 8px; }
            .route {
              display: grid; grid-template-columns: 68px 90px 1fr;
              gap: 10px; padding: 9px 0; border-top: 1px solid var(--line);
            }
            .route:first-child { border-top: 0; }
            code { color: var(--blue); font: 12px ui-monospace, monospace; overflow-wrap: anywhere; }
            footer { padding: 12px 0 32px; color: var(--muted); font-size: 12px; }
            @media (max-width: 780px) {
              header { align-items: start; flex-direction: column; }
              .metrics { grid-template-columns: 1fr 1fr; }
              .grid { grid-template-columns: 1fr; }
            }
          </style>
        </head>
        <body>
          <header>
            <div>
              <h1><small>Local read-only console</small>Rail Route Helper</h1>
              <p class="subtle">Live Operations · 仅监听本机存档，不控制游戏</p>
            </div>
            <div class="connection"><i id="dot" class="dot"></i><span id="connection">正在连接</span></div>
          </header>
          <main>
            <section class="metrics">
              <div class="metric"><span>网络</span><strong id="network-count">0</strong></div>
              <div class="metric"><span>列车</span><strong id="train-count">0</strong></div>
              <div class="metric"><span>活动告警</span><strong id="alert-count">0</strong></div>
              <div class="metric"><span>最后序号</span><strong id="sequence">—</strong></div>
            </section>
            <section class="grid">
              <div class="panel">
                <h2>活动告警</h2>
                <div id="active-alerts" class="empty">暂无活动告警</div>
              </div>
              <div class="panel">
                <h2>最近恢复</h2>
                <div id="resolved-alerts" class="empty">暂无已恢复告警</div>
              </div>
            </section>
            <section>
              <h2>实时运行图</h2>
              <div id="networks" class="empty">等待第一份 Operations 报告…</div>
            </section>
            <footer id="updated">尚未接收数据</footer>
          </main>
          <script>
            const byId = id => document.getElementById(id);
            const node = (tag, className, value) => {
              const element = document.createElement(tag);
              if (className) element.className = className;
              if (value !== undefined) element.textContent = value;
              return element;
            };
            const locationText = value => {
              if (!value) return "—";
              return value.platformNumber == null
                ? value.stationName
                : `${value.stationName} · ${value.platformNumber}道`;
            };
            const renderAlerts = (target, alerts, emptyText) => {
              target.replaceChildren();
              if (!alerts.length) {
                target.className = "empty";
                target.textContent = emptyText;
                return;
              }
              target.className = "";
              for (const alert of alerts) {
                const row = node("div", "alert");
                row.append(node("span", `badge ${alert.status === "resolved" ? "resolved" : ""}`, alert.status));
                const description = node("div");
                description.append(node("div", "", alert.summary));
                description.append(node("div", "subtle", `${alert.reportingNumber ?? "—"} · 观察 ${alert.observationCount} 次`));
                row.append(description);
                row.append(node("span", "subtle", `#${alert.lastObservedSequence}`));
                target.append(row);
              }
            };
            const renderNetworks = networks => {
              const target = byId("networks");
              target.replaceChildren();
              if (!networks.length) {
                target.className = "empty";
                target.textContent = "等待第一份 Operations 报告…";
                return;
              }
              target.className = "";
              for (const network of networks) {
                const card = node("article", "network");
                const head = node("div", "network-head");
                const title = node("div");
                title.append(node("h3", "", network.sourceSaveName));
                title.append(node("div", "subtle", `${network.gameVersion} · ${network.networkId.slice(0, 16)}…`));
                head.append(title);
                head.append(node("span", "subtle", `序号 ${network.sequence}`));
                card.append(head);
                const body = node("div", "network-body");
                const table = node("table");
                const labels = ["车次", "状态", "当前位置", "下一站", "进路可达"];
                const thead = node("thead");
                const headRow = node("tr");
                labels.forEach(label => headRow.append(node("th", "", label)));
                thead.append(headRow);
                table.append(thead);
                const tbody = node("tbody");
                for (const train of network.trains) {
                  const row = node("tr");
                  [
                    train.reportingNumber,
                    train.status,
                    locationText(train.currentLocation),
                    locationText(train.nextDestination),
                    train.reachability
                  ].forEach(value => row.append(node("td", "", value)));
                  tbody.append(row);
                }
                table.append(tbody);
                body.append(table);
                const routes = node("div", "route-list");
                routes.append(node("h3", "", "最近进路变化"));
                for (const item of network.recentRouteChanges.slice(-8).reverse()) {
                  const row = node("div", "route");
                  row.append(node("span", "badge", item.change.kind));
                  row.append(node("span", "subtle", `#${item.sequence}`));
                  row.append(node("code", "", item.change.controlNodeId));
                  routes.append(row);
                }
                if (!network.recentRouteChanges.length) {
                  routes.append(node("div", "empty", "暂无进路变化"));
                }
                body.append(routes);
                card.append(body);
                target.append(card);
              }
            };
            const render = state => {
              const active = state.alerts.filter(item => item.status === "active");
              const resolved = state.alerts.filter(item => item.status === "resolved").slice(0, 12);
              byId("network-count").textContent = state.networks.length;
              byId("train-count").textContent = state.networks.reduce((sum, item) => sum + item.trains.length, 0);
              byId("alert-count").textContent = active.length;
              byId("sequence").textContent = state.lastSequence ?? "—";
              byId("updated").textContent = state.lastUpdatedAtUtc
                ? `最后更新 ${new Date(state.lastUpdatedAtUtc).toLocaleString()}`
                : "尚未接收数据";
              renderAlerts(byId("active-alerts"), active, "暂无活动告警");
              renderAlerts(byId("resolved-alerts"), resolved, "暂无已恢复告警");
              renderNetworks(state.networks);
            };
            const refresh = async () => {
              try {
                const response = await fetch("/api/live", { cache: "no-store" });
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                render(await response.json());
                byId("dot").className = "dot live";
                byId("connection").textContent = "本机数据流正常";
              } catch (error) {
                byId("dot").className = "dot error";
                byId("connection").textContent = `连接失败：${error.message}`;
              }
            };
            refresh();
            setInterval(refresh, 1500);
          </script>
        </body>
        </html>
        """;
}

namespace Demo;

/// <summary>The single page of the demo. It asks for the state once a second and redraws.</summary>
internal static class DashboardPage
{
    public const string Html =
        """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>SyncLane demo</title>
        <style>
          :root {
            color-scheme: light dark;
            --page: #f6f6f4; --card: #ffffff; --ink: #1d1d1b; --soft: #6e6e69; --line: #e2e2dd;
            --pending: #8a8a84; --running: #2563c9; --completed: #2f8f4e; --failed: #c27a10; --deferred: #7c4fc4; --abandoned: #c0392b;
          }
          @media (prefers-color-scheme: dark) {
            :root { --page: #141413; --card: #1c1c1a; --ink: #ecece8; --soft: #9c9c95; --line: #2c2c29; }
          }
          * { box-sizing: border-box; }
          body { margin: 0; padding: 24px; background: var(--page); color: var(--ink); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
          header { display: flex; flex-wrap: wrap; gap: 12px; align-items: baseline; justify-content: space-between; margin-bottom: 18px; }
          h1 { margin: 0; font-size: 20px; }
          h1 small { margin-left: 10px; color: var(--soft); font-size: 13px; font-weight: 400; }
          button { padding: 7px 12px; border: 1px solid var(--line); border-radius: 6px; background: var(--card); color: var(--ink); font: inherit; cursor: pointer; }
          button:hover { border-color: var(--soft); }
          .accounts { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 12px; margin-bottom: 18px; }
          .account { padding: 12px 14px; border: 1px solid var(--line); border-radius: 8px; background: var(--card); }
          .account b { font-size: 15px; }
          .account .lane { color: var(--soft); font-size: 12px; }
          .account .numbers { margin-top: 6px; color: var(--soft); }
          .bar { height: 6px; margin-top: 10px; border-radius: 3px; background: var(--line); overflow: hidden; }
          .bar i { display: block; height: 100%; background: var(--completed); transition: width .4s; }
          .scroll { overflow-x: auto; border: 1px solid var(--line); border-radius: 8px; background: var(--card); }
          table { width: 100%; border-collapse: collapse; white-space: nowrap; }
          th, td { padding: 7px 10px; border-bottom: 1px solid var(--line); text-align: left; }
          th { color: var(--soft); font-size: 12px; font-weight: 600; }
          tr:last-child td { border-bottom: 0; }
          td.number { text-align: right; font-variant-numeric: tabular-nums; }
          td.error { max-width: 340px; overflow: hidden; text-overflow: ellipsis; color: var(--soft); }
          .pill { display: inline-block; min-width: 82px; padding: 1px 8px; border-radius: 10px; color: #fff; font-size: 12px; text-align: center; }
          .pending { background: var(--pending); } .running { background: var(--running); } .completed { background: var(--completed); }
          .failed { background: var(--failed); } .deferred { background: var(--deferred); } .abandoned { background: var(--abandoned); }
          footer { margin-top: 12px; color: var(--soft); font-size: 12px; }
        </style>
        </head>
        <body>
        <header>
          <h1>SyncLane demo <small id="summary"></small></h1>
          <div>
            <button id="connect">Connect another account</button>
            <button id="requeue">Requeue stopped jobs</button>
          </div>
        </header>
        <section class="accounts" id="accounts"></section>
        <div class="scroll">
          <table>
            <thead>
              <tr>
                <th>id</th><th>owner</th><th>lane</th><th>type</th><th>mode</th><th>status</th><th>window</th>
                <th>cursor</th><th>processed</th><th>attempts</th><th>held by / next run</th><th>error</th>
              </tr>
            </thead>
            <tbody id="jobs"></tbody>
          </table>
        </div>
        <footer>Every row is a row of <code>synclane.jobs</code>. Jobs that are at work or in trouble are listed first.</footer>
        <script>
          const order = { running: 0, failed: 1, deferred: 2, abandoned: 3, pending: 4, completed: 5 };
          const shown = 80;

          function element(tag, text, className) {
            const node = document.createElement(tag);
            if (text !== undefined && text !== null) node.textContent = text;
            if (className) node.className = className;
            return node;
          }

          function moment(value) {
            if (!value) return '';
            const text = new Date(value).toISOString();
            return text.endsWith('T00:00:00.000Z') ? text.slice(0, 10) : text.slice(0, 19).replace('T', ' ');
          }

          function clock(value) {
            return value ? new Date(value).toISOString().slice(11, 19) : '';
          }

          function holder(job, status) {
            if (status === 'running') return job.leasedBy;
            if (status === 'failed' || status === 'pending') return 'due ' + clock(job.nextRunAt);
            return '';
          }

          function drawAccounts(accounts) {
            const section = document.getElementById('accounts');
            section.replaceChildren(...accounts.map(account => {
              const card = element('div', null, 'account');
              card.append(element('b', account.key), element('div', 'lane ' + account.lane, 'lane'));
              card.append(element('div', account.orders + ' orders, ' + account.products + ' products', 'numbers'));
              card.append(element('div', account.loading
                ? 'first load ' + account.initialCompleted + ' of ' + account.initialJobs
                : 'first load done, ' + account.initialCompleted + ' of ' + account.initialJobs, 'numbers'));
              const bar = element('div', null, 'bar');
              const fill = element('i');
              fill.style.width = (account.initialJobs ? 100 * account.initialCompleted / account.initialJobs : 0) + '%';
              bar.append(fill);
              card.append(bar);
              return card;
            }));
          }

          function drawJobs(jobs) {
            const sorted = jobs
              .map(job => ({ job, status: job.status.toLowerCase() }))
              .sort((a, b) => order[a.status] - order[b.status] || b.job.id - a.job.id)
              .slice(0, shown);

            document.getElementById('jobs').replaceChildren(...sorted.map(({ job, status }) => {
              const row = element('tr');
              const pill = element('td');
              pill.append(element('span', status, 'pill ' + status));
              row.append(
                element('td', job.id, 'number'),
                element('td', job.owner),
                element('td', job.lane),
                element('td', job.type),
                element('td', job.mode.toLowerCase()),
                pill,
                element('td', job.windowStart ? moment(job.windowStart) + ' → ' + moment(job.windowEnd) : ''),
                element('td', job.cursor),
                element('td', job.processedCount, 'number'),
                element('td', job.attemptCount, 'number'),
                element('td', holder(job, status)),
                element('td', job.error, 'error'));
              return row;
            }));
          }

          async function refresh() {
            const state = await (await fetch('/api/state')).json();
            const counts = {};
            for (const job of state.jobs) counts[job.status.toLowerCase()] = (counts[job.status.toLowerCase()] || 0) + 1;
            document.getElementById('summary').textContent = Object.keys(order)
              .filter(status => counts[status])
              .map(status => counts[status] + ' ' + status)
              .join(' · ');
            drawAccounts(state.accounts);
            drawJobs(state.jobs);
          }

          document.getElementById('connect').onclick = () => fetch('/api/accounts', { method: 'POST' }).then(refresh);
          document.getElementById('requeue').onclick = () => fetch('/api/requeue', { method: 'POST' }).then(refresh);
          setInterval(() => refresh().catch(() => {}), 1000);
          refresh();
        </script>
        </body>
        </html>
        """;
}

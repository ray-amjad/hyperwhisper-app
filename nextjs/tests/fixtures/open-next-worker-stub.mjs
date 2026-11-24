// Stands in for `.open-next/worker.js` (made only by `opennextjs-cloudflare
// build`) when tests import worker.ts. Each test sets `globalThis.__openNextStub`.
export default {
  fetch: (...args) => globalThis.__openNextStub(...args),
};
export class DOQueueHandler {}
export class DOShardedTagCache {}

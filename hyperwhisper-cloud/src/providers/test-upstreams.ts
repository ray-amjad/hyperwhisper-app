// Test-only upstreams for the LLM timeout tests (#782). Real sockets on
// 127.0.0.1, so the code under test runs the real fetch and the real abort.
// Not imported by any production module.

export type TestUpstream = { url: string; stop: () => void };

/** Accepts the connection, reads the request, and never writes a byte. */
export function silentUpstream(): TestUpstream {
  const server = Bun.listen({
    hostname: '127.0.0.1',
    port: 0,
    socket: { data() {}, open() {} },
  });
  return { url: `http://127.0.0.1:${server.port}`, stop: () => server.stop(true) };
}

/** Sends 200 headers and part of a JSON body, then stalls with the socket open. */
export function stalledBodyUpstream(): TestUpstream {
  const server = Bun.listen({
    hostname: '127.0.0.1',
    port: 0,
    socket: {
      open() {},
      data(socket) {
        socket.write(
          'HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: 1000\r\n\r\n{"choices":',
        );
      },
    },
  });
  return { url: `http://127.0.0.1:${server.port}`, stop: () => server.stop(true) };
}

/** A URL on a port nothing listens on, so fetch() fails with a network error. */
export function refusedUrl(): string {
  const server = Bun.listen({ hostname: '127.0.0.1', port: 0, socket: { data() {} } });
  const url = `http://127.0.0.1:${server.port}`;
  server.stop(true);
  return url;
}

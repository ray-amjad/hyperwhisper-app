//
//  APIServerSettingsSection.swift
//  hyperwhisper
//
//  Settings → Local API. Off by default. Toggle on to start a localhost-only
//  HTTP server that exposes /health, /models, /modes, /transcribe,
//  /post-process, /recordings/search so MCP clients, benchmarking scripts,
//  and Shortcuts/Raycast can drive HyperWhisper.
//

import SwiftUI
import AppKit

struct APIServerSettingsSection: View {
    @AppStorage(LocalAPIServerEnabledKey) private var enabled: Bool = false
    @ObservedObject private var server = LocalAPIServer.shared

    @State private var revealToken: Bool = false
    @State private var selectedTab: Tab = .connection

    private static let docsURL = URL(string: "https://hyperwhisper.com/docs/api-reference/local-api/overview")!
    private static let mcpDocsURL = URL(string: "https://hyperwhisper.com/docs/api-reference/local-api/mcp-setup")!

    enum Tab: String, CaseIterable, Identifiable {
        case connection
        case mcp
        case curl

        var id: String { rawValue }
        var label: String {
            switch self {
            case .connection: return "Connection"
            case .mcp: return "MCP setup"
            case .curl: return "cURL"
            }
        }
    }

    var body: some View {
        SettingsSection(title: "Local API") {
            blurb
            topBar
            if enabled {
                tabBar
                tabContent
            }
        }
    }

    // MARK: - Blurb

    private var blurb: some View {
        Text("An MCP server + HTTP API on 127.0.0.1 for Claude Code, Cursor, benchmarking, and Shortcuts. Off by default.")
            .font(.callout)
            .foregroundColor(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 4)
    }

    // MARK: - Top compact bar

    private var topBar: some View {
        SettingsCard(horizontalPadding: 14) {
            HStack(spacing: 14) {
                Toggle("", isOn: Binding(
                    get: { enabled },
                    set: { newValue in
                        enabled = newValue
                        if newValue {
                            LocalAPIServer.shared.start()
                        } else {
                            LocalAPIServer.shared.stop()
                        }
                    }
                ))
                .labelsHidden()
                .toggleStyle(.switch)

                VStack(alignment: .leading, spacing: 2) {
                    Text(enabled ? "Server enabled" : "Server disabled")
                        .font(.system(size: 13, weight: .semibold))
                    Text(statusLine)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }

                Spacer(minLength: 8)

                Button {
                    NSWorkspace.shared.open(Self.docsURL)
                } label: {
                    Label("Docs", systemImage: "arrow.up.right.square")
                        .labelStyle(.titleAndIcon)
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
            }
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var statusLine: String {
        if !enabled { return "No network sockets bound." }
        if server.isRunning {
            let portText = server.listeningPort > 0 ? "127.0.0.1:\(server.listeningPort)" : "127.0.0.1"
            let tokenSuffix = server.bearerToken.suffix(4)
            return "\(portText) · token ••••\(tokenSuffix)"
        }
        return "Starting…"
    }

    // MARK: - Tab bar (underline-style)

    private var tabBar: some View {
        HStack(spacing: 22) {
            ForEach(Tab.allCases) { tab in
                tabButton(tab)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 4)
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(Color.secondary.opacity(0.18))
                .frame(height: 1)
        }
    }

    private func tabButton(_ tab: Tab) -> some View {
        let isActive = selectedTab == tab
        return Button {
            selectedTab = tab
        } label: {
            VStack(spacing: 6) {
                Text(tab.label)
                    .font(.system(size: 13, weight: isActive ? .semibold : .regular))
                    .foregroundColor(isActive ? .primary : .secondary)
                Rectangle()
                    .fill(isActive ? Color.accentColor : Color.clear)
                    .frame(height: 2)
            }
            .padding(.top, 2)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    // MARK: - Tab content

    @ViewBuilder
    private var tabContent: some View {
        switch selectedTab {
        case .connection: connectionTab
        case .mcp: mcpTab
        case .curl: snippetTab(.curl)
        }
    }

    private var connectionTab: some View {
        SettingsCard(horizontalPadding: 14) {
            VStack(alignment: .leading, spacing: 0) {
                kvRow(label: "Port",
                      value: server.listeningPort > 0 ? "\(server.listeningPort)" : "—") {
                    Button("Copy") { copyToPasteboard("\(server.listeningPort)") }
                        .buttonStyle(.bordered).controlSize(.small)
                        .disabled(server.listeningPort == 0)
                }
                Divider().padding(.vertical, 8)

                kvRow(label: "Bearer token",
                      value: tokenDisplay,
                      monospaced: true) {
                    HStack(spacing: 6) {
                        Button(revealToken ? "Hide" : "Reveal") { revealToken.toggle() }
                            .buttonStyle(.bordered).controlSize(.small)
                            .disabled(server.bearerToken.isEmpty)
                        Button("Copy") { copyToPasteboard(server.bearerToken) }
                            .buttonStyle(.bordered).controlSize(.small)
                            .disabled(server.bearerToken.isEmpty)
                        Button(role: .destructive) {
                            LocalAPIServer.shared.regenerateBearerToken()
                        } label: {
                            Image(systemName: "arrow.triangle.2.circlepath")
                        }
                        .buttonStyle(.bordered).controlSize(.small)
                        .help("Regenerate token (invalidates the current one immediately)")
                    }
                }

                Divider().padding(.vertical, 8)

                kvRow(label: "Port file",
                      value: LocalAPIServer.portFileURL.path,
                      monospaced: true,
                      truncate: true) {
                    Button("Show") {
                        NSWorkspace.shared.activateFileViewerSelecting([LocalAPIServer.portFileURL])
                    }
                    .buttonStyle(.bordered).controlSize(.small)
                }

                if let error = server.lastError {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundColor(.orange)
                        Text(error)
                            .font(.caption)
                            .foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.top, 8)
                }
            }
            .padding(.vertical, 12)
        }
    }

    private var mcpTab: some View {
        SettingsCard(horizontalPadding: 14) {
            VStack(alignment: .leading, spacing: 10) {
                Text("Drop into Cursor, Claude Code, or Claude Desktop's `mcpServers` block. The wrapper auto-reads the port and token from the discovery file at startup — no secrets in client config.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                codeBlock(mcpSnippet)

                HStack {
                    Link(destination: Self.mcpDocsURL) {
                        Label("MCP setup guide", systemImage: "book")
                            .font(.caption)
                    }
                    Spacer()
                    Button("Copy") { copyToPasteboard(mcpSnippet) }
                        .buttonStyle(.bordered).controlSize(.small)
                }
            }
            .padding(.vertical, 12)
        }
    }

    private func snippetTab(_ language: SnippetLanguage) -> some View {
        let snippet = snippet(for: language)
        return SettingsCard(horizontalPadding: 14) {
            VStack(alignment: .leading, spacing: 10) {
                Text("Pre-filled with your port and token.")
                    .font(.caption)
                    .foregroundColor(.secondary)

                codeBlock(snippet)

                HStack {
                    Spacer()
                    Button("Copy") { copyToPasteboard(snippet) }
                        .buttonStyle(.bordered).controlSize(.small)
                }
            }
            .padding(.vertical, 12)
        }
    }

    // MARK: - Reusable bits

    private func kvRow<Trailing: View>(
        label: String,
        value: String,
        monospaced: Bool = false,
        truncate: Bool = false,
        @ViewBuilder trailing: () -> Trailing
    ) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(label)
                .font(.caption)
                .foregroundColor(.secondary)
                .frame(width: 100, alignment: .leading)
            Group {
                if monospaced {
                    Text(value).font(.system(.caption, design: .monospaced))
                } else {
                    Text(value).font(.system(size: 13))
                }
            }
            .textSelection(.enabled)
            .lineLimit(1)
            .truncationMode(truncate ? .middle : .tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            trailing()
        }
    }

    private func codeBlock(_ text: String) -> some View {
        Text(text)
            .font(.system(.caption, design: .monospaced))
            .textSelection(.enabled)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor).opacity(0.6)))
    }

    // MARK: - Snippets

    private enum SnippetLanguage {
        case curl
    }

    private func snippet(for language: SnippetLanguage) -> String {
        switch language {
        case .curl:
            return """
            PORT=\(server.listeningPort > 0 ? "\(server.listeningPort)" : "$(jq -r .port ~/Library/Application\\ Support/HyperWhisper/local-api.json)")
            TOKEN=\(server.bearerToken.isEmpty ? "$(jq -r .token ~/Library/Application\\ Support/HyperWhisper/local-api.json)" : server.bearerToken)
            curl -s http://127.0.0.1:$PORT/health | jq .
            curl -s -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:$PORT/models" | jq .
            """
        }
    }

    // MARK: - Helpers

    private var tokenDisplay: String {
        if server.bearerToken.isEmpty { return "<no token yet>" }
        if revealToken { return server.bearerToken }
        let masked = String(repeating: "•", count: max(server.bearerToken.count - 4, 0))
        return masked + server.bearerToken.suffix(4)
    }

    private var mcpSnippet: String {
        """
        {
          "mcpServers": {
            "hyperwhisper": {
              "command": "npx",
              "args": ["-y", "@hyperwhisper/mcp"]
            }
          }
        }
        """
    }

    private func copyToPasteboard(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }
}

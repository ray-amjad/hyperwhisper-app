"use client";

import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { Download, Calendar, HardDrive, Monitor, Sparkles } from "lucide-react";
import { useEffect, useState } from "react";

type Platform = "mac" | "windows";

/**
 * Interface representing a single download variant (one per architecture)
 */
interface DownloadVariant {
  arch: string; // e.g. "x64", "ARM64", or "" for macOS
  downloadUrl: string;
  fileSize: number;
}

/**
 * Interface representing a version with potentially multiple download variants
 * On macOS there's one variant per version; on Windows there are two (x64 + ARM64)
 */
interface Version {
  version: string;
  pubDate: string;
  buildNumber: string;
  minimumSystemVersion: string;
  downloads: DownloadVariant[];
}

/**
 * Downloads Page Component
 *
 * This page fetches and displays all available HyperWhisper versions from the appcast.xml feed.
 * Users can browse and download older versions of the application.
 *
 * Features:
 * - Parses Sparkle appcast.xml format
 * - Displays versions in reverse chronological order (newest first)
 * - Shows formatted release dates and file sizes
 * - Provides direct download links for each version
 * - Matches the support page width and design pattern
 */
export default function DownloadsPage() {
  const [versions, setVersions] = useState<Version[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedPlatform, setSelectedPlatform] = useState<Platform>("mac");

  /**
   * Fetches and parses the appcast.xml file on component mount
   * The XML is parsed using the browser's DOMParser API
   * Extracts version information from Sparkle-formatted XML entries
   */
  useEffect(() => {
    // Guard against out-of-order resolution when the platform changes while a
    // fetch is still in flight. Without this, a slow request for the previously
    // selected platform can resolve last and overwrite the current platform's
    // version list (e.g. macOS tab showing Windows downloads).
    let ignore = false;
    const controller = new AbortController();

    async function fetchVersions() {
      try {
        setLoading(true);
        setError(null);
        // Fetch the appcast.xml file from the public directory
        const feedPath =
          selectedPlatform === "windows"
            ? "/appcast-windows.xml"
            : "/appcast.xml";
        const response = await fetch(feedPath, { signal: controller.signal });

        if (!response.ok) {
          throw new Error(`Failed to fetch ${feedPath}`);
        }

        const xmlText = await response.text();

        // Parse XML using browser's DOMParser
        const parser = new DOMParser();
        const xmlDoc = parser.parseFromString(xmlText, "text/xml");

        // Extract all <item> elements and group by version number
        const items = xmlDoc.querySelectorAll("item");
        const versionMap = new Map<string, Version>();

        items.forEach((item) => {
          // Extract data from Sparkle namespace elements
          const title = item.querySelector("title")?.textContent || "";
          const pubDate = item.querySelector("pubDate")?.textContent || "";
          const buildNumber =
            item.querySelector("sparkle\\:version, version")?.textContent || "";
          const shortVersionString =
            item.querySelector(
              "sparkle\\:shortVersionString, shortVersionString",
            )?.textContent || "";
          const minimumSystemVersion =
            item.querySelector(
              "sparkle\\:minimumSystemVersion, minimumSystemVersion",
            )?.textContent || "";
          const os =
            item.querySelector("sparkle\\:os, os")?.textContent || "";
          const enclosure = item.querySelector("enclosure");
          const downloadUrl = enclosure?.getAttribute("url") || "";
          const fileSize = parseInt(enclosure?.getAttribute("length") || "0");

          if (!buildNumber || !downloadUrl) return;

          // Determine architecture label from sparkle:os (e.g. "windows-x64" -> "x64")
          let arch = "";
          if (os.includes("arm64")) arch = "ARM64";
          else if (os.includes("x64")) arch = "x64";

          const variant: DownloadVariant = { arch, downloadUrl, fileSize };

          const displayVersion = shortVersionString || title || buildNumber;

          if (versionMap.has(buildNumber)) {
            versionMap.get(buildNumber)!.downloads.push(variant);
          } else {
            versionMap.set(buildNumber, {
              version: displayVersion,
              pubDate,
              buildNumber,
              minimumSystemVersion,
              downloads: [variant],
            });
          }
        });

        if (ignore) return;
        setVersions(Array.from(versionMap.values()));
        setLoading(false);
      } catch (err) {
        // Ignore aborts triggered by cleanup / a newer platform selection.
        if (ignore || (err instanceof DOMException && err.name === "AbortError")) {
          return;
        }
        setError(err instanceof Error ? err.message : "An error occurred");
        setLoading(false);
      }
    }

    fetchVersions();

    return () => {
      ignore = true;
      controller.abort();
    };
  }, [selectedPlatform]);

  /**
   * Formats a date string from RFC 2822 format to a readable format
   * Example: "Sun, 12 Oct 2025 11:52:35 +0900" -> "October 12, 2025"
   */
  const formatDate = (dateString: string) => {
    try {
      const date = new Date(dateString);

      return date.toLocaleDateString("en-US", {
        year: "numeric",
        month: "long",
        day: "numeric",
      });
    } catch {
      return dateString;
    }
  };

  /**
   * Formats file size from bytes to human-readable format
   * Example: 16926355 -> "16.14 MB"
   */
  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return "Unknown";
    const mb = bytes / (1024 * 1024);

    return `${mb.toFixed(2)} MB`;
  };

  /**
   * Determines if a version is the latest (first in the list)
   */
  const isLatestVersion = (index: number) => index === 0;

  const platformLabel = selectedPlatform === "mac" ? "macOS" : "Windows";
  const downloadLabel =
    selectedPlatform === "mac" ? "Download DMG" : "Download EXE";

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 px-6 py-20">
      <div className="max-w-3xl mx-auto">
        {/* Page Header */}
        <div className="text-center mb-12">
          <h1 className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            Older Versions
          </h1>
          <p className="text-lg text-gray-400">
            Browse and download all releases of HyperWhisper
          </p>
          <div className="mt-4 inline-flex items-center gap-2 bg-gray-800/70 border border-gray-700 rounded-full px-2 py-1">
            <Button
              className={`text-sm px-4 py-2 rounded-full ${
                selectedPlatform === "mac"
                  ? "bg-purple-600 text-white"
                  : "text-gray-300 bg-transparent"
              }`}
              size="sm"
              variant="light"
              onPress={() => setSelectedPlatform("mac")}
            >
              macOS
            </Button>
            <Button
              className={`text-sm px-4 py-2 rounded-full ${
                selectedPlatform === "windows"
                  ? "bg-purple-600 text-white"
                  : "text-gray-300 bg-transparent"
              }`}
              size="sm"
              variant="light"
              onPress={() => setSelectedPlatform("windows")}
            >
              Windows
              <span className="ml-1.5 text-[10px] font-medium text-yellow-400 bg-yellow-400/20 px-1.5 py-0.5 rounded">BETA</span>
            </Button>
          </div>
        </div>

        {/* Loading State */}
        {loading && (
          <div className="text-center text-gray-400 py-12">
            <div className="inline-block animate-spin rounded-full h-12 w-12 border-b-2 border-purple-500 mb-4" />
            <p className="text-lg">Loading versions...</p>
          </div>
        )}

        {/* Error State */}
        {error && (
          <div className="text-center">
            <Card className="bg-red-900/20 backdrop-blur-xl border-red-800">
              <CardBody className="p-8">
                <p className="text-red-400 text-lg">
                  Error loading versions: {error}
                </p>
              </CardBody>
            </Card>
          </div>
        )}

        {/* Versions List */}
        {!loading && !error && (
          <>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-12">
              {versions.map((version, index) => (
                <Card
                  key={version.buildNumber}
                  className={`bg-gray-900/50 backdrop-blur-xl border-gray-800 hover:border-purple-700/50 transition-all duration-300 h-full ${
                    isLatestVersion(index)
                      ? "ring-2 ring-purple-500/50 shadow-lg shadow-purple-500/20"
                      : ""
                  }`}
                >
                  <CardBody className="p-6">
                    {/* Version Header */}
                    <div className="flex items-start justify-between mb-4">
                      <div className="flex items-start gap-3 flex-1">
                        <div
                          className={`p-2.5 rounded-lg ${
                            isLatestVersion(index)
                              ? "bg-gradient-to-br from-purple-600 to-blue-600"
                              : "bg-purple-900/30"
                          }`}
                        >
                          {isLatestVersion(index) ? (
                            <Sparkles className="w-5 h-5 text-white" />
                          ) : (
                            <Download className="w-5 h-5 text-purple-400" />
                          )}
                        </div>
                        <div className="flex-1">
                          <div className="flex items-center gap-2 mb-1">
                            <h3 className="text-xl font-bold text-white">
                              v{version.version}
                            </h3>
                            {isLatestVersion(index) && (
                              <span className="px-2 py-0.5 text-xs font-semibold bg-gradient-to-r from-purple-600 to-blue-600 text-white rounded-full">
                                Latest
                              </span>
                            )}
                          </div>
                          <p className="text-sm text-gray-500">
                            Build {version.buildNumber}
                          </p>
                        </div>
                      </div>
                    </div>

                    {/* Version Details */}
                    <div className="space-y-3 mb-5">
                      <div className="flex items-center gap-2 text-gray-400">
                        <Calendar className="w-4 h-4 text-purple-400" />
                        <span className="text-sm">
                          {formatDate(version.pubDate)}
                        </span>
                      </div>
                      <div className="flex items-center gap-2 text-gray-400">
                        <HardDrive className="w-4 h-4 text-purple-400" />
                        <span className="text-sm">
                          {formatFileSize(version.downloads[0]?.fileSize ?? 0)}
                        </span>
                      </div>
                      <div className="flex items-center gap-2 text-gray-400">
                        <Monitor className="w-4 h-4 text-purple-400" />
                        <span className="text-sm">
                          {platformLabel} {version.minimumSystemVersion}+
                        </span>
                      </div>
                    </div>

                    {/* Download Button(s) */}
                    <div className={`flex gap-2 ${version.downloads.length > 1 ? "flex-col sm:flex-row" : ""}`}>
                      {version.downloads.map((dl) => (
                        <Button
                          key={dl.arch || "default"}
                          as="a"
                          className={`flex-1 ${
                            isLatestVersion(index)
                              ? "bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold"
                              : "bg-purple-600/20 hover:bg-purple-600/30 text-purple-300"
                          }`}
                          href={dl.downloadUrl}
                          size="lg"
                          startContent={<Download className="w-4 h-4" />}
                        >
                          {dl.arch ? `${dl.arch}` : downloadLabel}
                        </Button>
                      ))}
                    </div>
                  </CardBody>
                </Card>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

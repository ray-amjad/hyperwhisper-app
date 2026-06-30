"use client";

import { useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { Download, Copy, Check, Terminal, CheckCircle } from "lucide-react";
import { useTranslations } from "next-intl";

type Platform = "mac" | "windows";

export default function DownloadPage() {
  const t = useTranslations("downloadPage");
  const searchParams = useSearchParams();
  const [selectedPlatform, setSelectedPlatform] = useState<Platform>("mac");
  const [downloadState, setDownloadState] = useState<
    Record<
      Platform,
      { url: string | null; countdown: number; started: boolean }
    >
  >({
    mac: { url: null, countdown: 5, started: false },
    windows: { url: null, countdown: 5, started: false },
  });
  const [copied, setCopied] = useState(false);

  const currentState = downloadState[selectedPlatform];

  // Detect user's OS on mount and set platform accordingly
  // URL query param takes precedence over OS detection
  useEffect(() => {
    // Check URL param first
    const platformParam = searchParams.get("platform");

    if (platformParam === "windows") {
      setSelectedPlatform("windows");

      return;
    }
    if (platformParam === "mac") {
      setSelectedPlatform("mac");

      return;
    }

    // Fall back to OS detection
    const platform = navigator.platform?.toLowerCase() || "";
    const userAgent = navigator.userAgent?.toLowerCase() || "";

    if (platform.includes("win") || userAgent.includes("windows")) {
      setSelectedPlatform("windows");
    }
    // macOS is default, no change needed
  }, [searchParams]);

  // Fetch the download URL on mount (but don't trigger download yet)
  // Skip pre-fetching for Windows since we have explicit architecture buttons
  useEffect(() => {
    // Windows uses explicit architecture buttons, no need to pre-fetch
    if (selectedPlatform === "windows") return;

    const fetchDownloadUrl = async () => {
      try {
        const response = await fetch(
          `/api/download?platform=${selectedPlatform}`,
          {
            redirect: "follow",
          },
        );
        const url =
          response.url || `/api/download?platform=${selectedPlatform}`;

        setDownloadState((prev) => ({
          ...prev,
          [selectedPlatform]: { ...prev[selectedPlatform], url },
        }));
      } catch (error) {
        console.error("Failed to fetch download URL:", error);
        setDownloadState((prev) => ({
          ...prev,
          [selectedPlatform]: {
            ...prev[selectedPlatform],
            url: `/api/download?platform=${selectedPlatform}`,
          },
        }));
      }
    };

    // Only fetch if we don't have a URL yet
    if (!currentState.url) {
      fetchDownloadUrl();
    }
  }, [selectedPlatform, currentState.url]);

  // Reset countdown when switching platforms
  useEffect(() => {
    setDownloadState((prev) => ({
      ...prev,
      [selectedPlatform]: {
        ...prev[selectedPlatform],
        countdown: 5,
        started: false,
      },
    }));
  }, [selectedPlatform]);

  // Countdown timer effect
  // DELAY REASON: Browsers block immediate auto-downloads as a security measure
  // to prevent drive-by downloads. A countdown gives visual feedback and signals
  // to the browser this is an intentional user-initiated action.
  // Skip countdown entirely for Windows - users choose architecture manually.
  useEffect(() => {
    // Windows uses explicit architecture buttons, no auto-download
    if (selectedPlatform === "windows") return;

    const { countdown, started } = currentState;

    // Don't continue countdown if download already started manually
    if (started) return;

    if (countdown > 0) {
      const timerId = setTimeout(() => {
        setDownloadState((prev) => ({
          ...prev,
          [selectedPlatform]: {
            ...prev[selectedPlatform],
            countdown: prev[selectedPlatform].countdown - 1,
          },
        }));
      }, 1000);

      return () => clearTimeout(timerId);
    } else {
      // Countdown finished, trigger download
      triggerDownload(selectedPlatform);
    }
  }, [currentState.countdown, currentState.started, selectedPlatform]);

  const triggerDownload = (platform: Platform) => {
    const state = downloadState[platform];

    if (state.started) return;
    setDownloadState((prev) => ({
      ...prev,
      [platform]: { ...prev[platform], started: true, countdown: 0 },
    }));

    const url = state.url || `/api/download?platform=${platform}`;

    window.location.href = url;
  };

  const handleManualDownload = () => {
    triggerDownload(selectedPlatform);
  };

  const handleCopyCommand = async () => {
    try {
      await navigator.clipboard.writeText(t("homebrewCommand"));
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (error) {
      console.error("Failed to copy:", error);
    }
  };

  const platformLabel = selectedPlatform === "mac" ? "macOS" : "Windows";

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 px-6 py-20">
      <div className="max-w-2xl mx-auto">
        {/* Header */}
        <div className="text-center mb-6">
          {/* App Icon */}
          <div className="w-24 h-24 mx-auto mb-8">
            <div className="w-full h-full bg-gradient-to-b from-gray-700 to-gray-900 rounded-2xl flex items-center justify-center shadow-2xl">
              <img
                alt="HyperWhisper Logo"
                className="w-24 h-24 rounded-xl"
                src="/icon/256.png"
              />
            </div>
          </div>

          <h1 className="text-3xl md:text-4xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h1>

          <div className="inline-flex items-center gap-2 bg-gray-800/70 border border-gray-700 rounded-full px-2 py-1 mb-6">
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
            </Button>
          </div>

          {/* Countdown or status message */}
          {selectedPlatform === "windows" ? (
            <p className="text-lg text-gray-400">{t("selectArchitecture")}</p>
          ) : currentState.started ? (
            <div className="flex items-center justify-center gap-2 text-gray-400">
              <CheckCircle className="w-5 h-5 text-green-500" />
              <span>{t("downloadStarted")}</span>
            </div>
          ) : (
            <p className="text-lg text-gray-400">
              {t("countdownText", { seconds: currentState.countdown })}
            </p>
          )}
        </div>

        {/* Download Button(s) */}
        <div className="flex justify-center mb-10">
          {selectedPlatform === "windows" ? (
            <div className="flex flex-col sm:flex-row gap-4">
              <Button
                as="a"
                className="bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold hover:from-purple-500 hover:to-blue-500 transition-all hover:shadow-lg px-8"
                href="/api/download?platform=windows&arch=x64"
                size="lg"
                startContent={<Download className="w-5 h-5" />}
              >
                {t("downloadX64")}
              </Button>
              <Button
                as="a"
                className="bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold hover:from-purple-500 hover:to-blue-500 transition-all hover:shadow-lg px-8"
                href="/api/download?platform=windows&arch=arm64"
                size="lg"
                startContent={<Download className="w-5 h-5" />}
              >
                {t("downloadArm64")}
              </Button>
            </div>
          ) : (
            <Button
              className="bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold hover:from-purple-500 hover:to-blue-500 transition-all hover:shadow-lg px-8"
              size="lg"
              startContent={<Download className="w-5 h-5" />}
              onClick={handleManualDownload}
            >
              {currentState.started ? t("downloadAgain") : t("downloadNow")}
            </Button>
          )}
        </div>

        {/* OR Divider - only show for macOS */}
        {selectedPlatform === "mac" && (
          <div className="flex items-center gap-4 mb-10">
            <div className="flex-1 h-px bg-gray-700" />
            <span className="text-gray-500 text-sm font-medium uppercase tracking-wider">
              {t("orDivider")}
            </span>
            <div className="flex-1 h-px bg-gray-700" />
          </div>
        )}

        {/* Homebrew Section - only show for macOS */}
        {selectedPlatform === "mac" && (
          <Card className="bg-gray-900/50 backdrop-blur-xl border border-gray-800">
            <CardBody className="p-6">
              {/* Section Header */}
              <div className="flex items-center gap-3 mb-4">
                <div className="w-10 h-10 flex items-center justify-center rounded-lg bg-gradient-to-br from-orange-500/20 to-yellow-500/20 border border-orange-500/30">
                  <Terminal className="w-5 h-5 text-orange-400" />
                </div>
                <h2 className="text-lg font-semibold text-white">
                  {t("homebrewTitle")}
                </h2>
              </div>

              {/* Command Box */}
              <div className="flex items-center gap-2 rounded-lg border border-gray-700 bg-gray-800/80 p-3">
                <code className="flex-1 text-sm font-mono text-gray-200 overflow-x-auto">
                  {t("homebrewCommand")}
                </code>
                <Button
                  isIconOnly
                  className="bg-gray-700 hover:bg-gray-600 text-gray-300 min-w-[80px] w-auto px-3"
                  size="sm"
                  variant="flat"
                  onClick={handleCopyCommand}
                >
                  {copied ? (
                    <>
                      <Check className="w-4 h-4 text-green-400 mr-1" />
                      <span className="text-green-400 text-xs">
                        {t("copied")}
                      </span>
                    </>
                  ) : (
                    <>
                      <Copy className="w-4 h-4 mr-1" />
                      <span className="text-xs">{t("copy")}</span>
                    </>
                  )}
                </Button>
              </div>
            </CardBody>
          </Card>
        )}
      </div>
    </div>
  );
}

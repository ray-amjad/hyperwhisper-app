"use client";

import { useState } from "react";

interface License {
  id: string;
  key: string;
  status: string;
}

interface LicenseKeysCardProps {
  licenses: License[];
}

/**
 * License Keys Card
 *
 * Displays all license keys in a single card with show/hide and copy functionality.
 */
export default function LicenseKeysCard({ licenses }: LicenseKeysCardProps) {
  const [showKeys, setShowKeys] = useState<Record<string, boolean>>({});
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  const maskLicenseKey = (key: string) => {
    const segments = key.split("-");
    if (segments.length <= 2) return key;
    return segments.slice(0, 2).join("-") + "-" + segments.slice(2).map(() => "••••").join("-");
  };

  const copyToClipboard = async (key: string) => {
    await navigator.clipboard.writeText(key);
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
  };

  const toggleShowKey = (id: string) => {
    setShowKeys((prev) => ({ ...prev, [id]: !prev[id] }));
  };

  return (
    <div className="bg-white/5 rounded-xl border border-white/10 p-5">
      <p className="text-sm text-gray-400 mb-3">License Keys</p>
      <div className="space-y-3">
        {licenses.map((license) => {
          const isActive = license.status === "granted";
          const isShown = showKeys[license.id];
          const isCopied = copiedKey === license.key;

          return (
            <div key={license.id} className="flex items-center justify-between gap-3">
              <div className="flex items-center gap-2 min-w-0 flex-1">
                <code className="font-mono text-white bg-white/10 px-3 py-1.5 rounded text-sm truncate">
                  {isShown ? license.key : maskLicenseKey(license.key)}
                </code>
                <button
                  onClick={() => toggleShowKey(license.id)}
                  className="p-1.5 text-gray-400 hover:text-white hover:bg-white/10 rounded transition-colors shrink-0"
                  title={isShown ? "Hide license key" : "Show license key"}
                >
                  {isShown ? (
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l3.59 3.59m0 0A9.953 9.953 0 0112 5c4.478 0 8.268 2.943 9.543 7a10.025 10.025 0 01-4.132 5.411m0 0L21 21" />
                    </svg>
                  ) : (
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                    </svg>
                  )}
                </button>
                <button
                  onClick={() => copyToClipboard(license.key)}
                  className="p-1.5 text-gray-400 hover:text-white hover:bg-white/10 rounded transition-colors shrink-0"
                  title="Copy to clipboard"
                >
                  {isCopied ? (
                    <svg className="w-4 h-4 text-green-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                    </svg>
                  ) : (
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                    </svg>
                  )}
                </button>
              </div>
              <span
                className={`px-2 py-1 text-xs font-medium rounded shrink-0 ${
                  isActive
                    ? "bg-green-500/20 text-green-400"
                    : "bg-gray-500/20 text-gray-400"
                }`}
              >
                {isActive ? "Active" : "Inactive"}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

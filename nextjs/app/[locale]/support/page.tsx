"use client";

import { Card, CardBody } from "@heroui/card";
import { Mail, Clock } from "lucide-react";
import { useTranslations } from "next-intl";

export default function SupportPage() {
  const t = useTranslations("support");
  const emailAddress = "support@hyperwhisper.com";
  const subject = t("emailTemplate.subject");
  const body = t("emailTemplate.body");

  const mailtoLink = `mailto:${emailAddress}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 px-6 py-20">
      <div className="max-w-2xl mx-auto">
        <div className="text-center mb-16">
          <h1 className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h1>
          <p className="text-lg text-gray-400">{t("subtitle")}</p>
        </div>

        <Card className="bg-gray-900/50 backdrop-blur-xl border border-gray-800">
          <CardBody className="p-8">
            {/* Header section */}
            <div className="flex flex-col items-center mb-8">
              {/* Icon */}
              <div className="w-16 h-16 mb-4 flex items-center justify-center rounded-full bg-gradient-to-br from-purple-500/20 to-pink-500/20 border border-purple-500/30">
                <Mail className="w-8 h-8 text-purple-400" />
              </div>

              <h2 className="text-2xl font-bold text-white mb-2 text-center">
                {t("getInTouch")}
              </h2>
              <p className="text-gray-400 text-center max-w-md">
                {t("description")}
              </p>
            </div>

            {/* Email display box */}
            <div className="rounded-lg border border-gray-700 bg-gray-800/50 p-4 mb-6">
              <p className="text-sm text-gray-400 mb-2">{t("emailUsAt")}</p>
              <p className="text-lg font-semibold text-white">{emailAddress}</p>
            </div>

            {/* Email client options */}
            <div className="space-y-3 mb-6">
              <p className="text-sm font-medium text-gray-300">
                {t("openInClient")}
              </p>

              {/* Default email client */}
              <a
                className="flex w-full items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-purple-600 to-pink-600 px-6 py-3 text-base font-semibold text-white transition-all hover:from-purple-500 hover:to-pink-500 hover:shadow-lg"
                href={mailtoLink}
              >
                <Mail className="h-4 w-4" />
                {t("defaultClient")}
              </a>

              {/* Gmail and Outlook options */}
              <div className="grid grid-cols-2 gap-2">
                <a
                  className="flex items-center justify-center gap-2 rounded-lg border border-gray-700 bg-gray-800 px-4 py-2.5 text-sm font-medium text-gray-300 transition-colors hover:bg-gray-700 hover:border-gray-600"
                  href={`https://mail.google.com/mail/?view=cm&fs=1&to=${emailAddress}&su=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                  rel="noopener noreferrer"
                  target="_blank"
                >
                  <svg
                    className="h-4 w-4"
                    fill="#EA4335"
                    role="img"
                    viewBox="0 0 24 24"
                  >
                    <path d="M24 5.457v13.909c0 .904-.732 1.636-1.636 1.636h-3.819V11.73L12 16.64l-6.545-4.91v9.273H1.636A1.636 1.636 0 0 1 0 19.366V5.457c0-2.023 2.309-3.178 3.927-1.964L12 9.545l8.073-6.052C21.69 2.28 24 3.434 24 5.457z" />
                  </svg>
                  {t("gmail")}
                </a>
                <a
                  className="flex items-center justify-center gap-2 rounded-lg border border-gray-700 bg-gray-800 px-4 py-2.5 text-sm font-medium text-gray-300 transition-colors hover:bg-gray-700 hover:border-gray-600"
                  href={`https://outlook.live.com/mail/0/deeplink/compose?to=${emailAddress}&subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                  rel="noopener noreferrer"
                  target="_blank"
                >
                  <Mail className="h-4 w-4 text-blue-400" />
                  {t("outlook")}
                </a>
              </div>
            </div>

            {/* Response time info */}
            <div className="flex items-start gap-3 text-gray-400 pt-4 border-t border-gray-700">
              <Clock className="w-5 h-5 mt-0.5 flex-shrink-0" />
              <p className="text-sm">{t("responseTime")}</p>
            </div>
          </CardBody>
        </Card>
      </div>
    </div>
  );
}

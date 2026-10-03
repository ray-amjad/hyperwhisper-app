"use client";

import { Card, CardBody } from "@heroui/card";
import { Mail, Clock } from "lucide-react";
import { useTranslations } from "next-intl";

export default function SupportPage() {
  const t = useTranslations("support");
  const emailAddress = "hi@support.hyperwhisper.com";
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
                className="relative isolate flex w-full items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-purple-600 to-pink-600 px-6 py-3 text-base font-semibold text-white transition-shadow hover:shadow-lg before:absolute before:inset-0 before:-z-10 before:rounded-[inherit] before:bg-gradient-to-r before:from-purple-500 before:to-pink-500 before:opacity-0 before:transition-opacity hover:before:opacity-100"
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
                    aria-hidden="true"
                    className="h-4 w-4 text-[#EA4335]"
                    fill="currentColor"
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
                  <svg
                    aria-hidden="true"
                    className="h-4 w-4 text-[#0078D4]"
                    fill="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path d="M7.88 12.04q0 .45-.11.87-.1.41-.33.74-.22.33-.58.52-.37.2-.87.2t-.85-.2q-.35-.21-.57-.55-.22-.33-.33-.75-.1-.42-.1-.86t.1-.87q.1-.43.34-.76.22-.34.59-.54.36-.2.87-.2t.86.2q.35.21.57.55.22.34.31.77.1.43.1.88zM24 12v9.38q0 .46-.33.8-.33.32-.8.32H7.13q-.46 0-.8-.33-.32-.33-.32-.8V18H1q-.41 0-.7-.3-.3-.29-.3-.7V7q0-.41.3-.7Q.58 6 1 6h6.5V2.55q0-.44.3-.75.3-.3.75-.3h12.9q.44 0 .75.3.3.3.3.75V10.85l1.24.72h.01q.1.07.18.18.07.12.07.25zm-6-8.25v3h3v-3zm0 4.5v3h3v-3zm0 4.5v1.83l3.05-1.83zm-5.25-9v3h3.75v-3zm0 4.5v3h3.75v-3zm0 4.5v2.03l2.41 1.5 1.34-.8v-2.73zM9 3.75V6h2l.13.01.12.04v-2.3zM5.98 15.98q.9 0 1.6-.3.7-.32 1.19-.86.48-.55.73-1.28.25-.74.25-1.61 0-.83-.25-1.55-.24-.71-.71-1.24t-1.15-.83q-.68-.3-1.55-.3-.92 0-1.64.3-.71.3-1.2.85-.5.54-.75 1.3-.25.74-.25 1.63 0 .85.26 1.56.26.72.74 1.23.48.52 1.17.81.69.3 1.56.3zM7.5 21h12.39L12 16.08V17q0 .41-.3.7-.29.3-.7.3H7.5zm15-.13v-7.24l-5.9 3.54Z" />
                  </svg>
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

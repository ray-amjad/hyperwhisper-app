"use client";

import { createContext, useContext, useState, ReactNode } from "react";

/** The platforms /download accepts as `?platform=`. */
export type DownloadPlatform = "mac" | "windows" | "linux";

interface DownloadModalContextType {
  isOpen: boolean;
  /**
   * The platform the visitor picked (a homepage "Download for Mac / Windows /
   * Linux" button), or null for a plain Download button, where /download
   * detects the OS itself.
   */
  platform: DownloadPlatform | null;
  openModal: () => void;
  openModalFor: (platform: DownloadPlatform) => void;
  closeModal: () => void;
}

const DownloadModalContext = createContext<
  DownloadModalContextType | undefined
>(undefined);

export function DownloadModalProvider({ children }: { children: ReactNode }) {
  const [isOpen, setIsOpen] = useState(false);
  const [platform, setPlatform] = useState<DownloadPlatform | null>(null);

  // openModal is passed straight to onClick in several places, so it takes no
  // argument: the click event must never be read as a platform.
  const openModal = () => {
    setPlatform(null);
    setIsOpen(true);
  };
  const openModalFor = (next: DownloadPlatform) => {
    setPlatform(next);
    setIsOpen(true);
  };
  const closeModal = () => setIsOpen(false);

  return (
    <DownloadModalContext.Provider
      value={{ isOpen, platform, openModal, openModalFor, closeModal }}
    >
      {children}
    </DownloadModalContext.Provider>
  );
}

export function useDownloadModal() {
  const context = useContext(DownloadModalContext);

  if (context === undefined) {
    throw new Error(
      "useDownloadModal must be used within a DownloadModalProvider",
    );
  }

  return context;
}

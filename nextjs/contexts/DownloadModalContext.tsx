"use client";

import { createContext, useContext, useState, ReactNode } from "react";

interface DownloadModalContextType {
  isOpen: boolean;
  openModal: () => void;
  closeModal: () => void;
}

const DownloadModalContext = createContext<
  DownloadModalContextType | undefined
>(undefined);

export function DownloadModalProvider({ children }: { children: ReactNode }) {
  const [isOpen, setIsOpen] = useState(false);

  const openModal = () => setIsOpen(true);
  const closeModal = () => setIsOpen(false);

  return (
    <DownloadModalContext.Provider value={{ isOpen, openModal, closeModal }}>
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

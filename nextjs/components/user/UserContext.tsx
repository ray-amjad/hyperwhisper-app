"use client";

import { createContext, useContext } from "react";

/**
 * User Context
 *
 * Provides user information (email, admin status) to child components
 * without prop drilling. Used by UserSidebar, UserHeader, and dashboard
 * components to conditionally render admin-only features.
 */

interface UserContextType {
  email: string;
  isAdmin: boolean;
}

const UserContext = createContext<UserContextType | null>(null);

export function UserProvider({
  children,
  email,
  isAdmin,
}: {
  children: React.ReactNode;
  email: string;
  isAdmin: boolean;
}) {
  return (
    <UserContext.Provider value={{ email, isAdmin }}>
      {children}
    </UserContext.Provider>
  );
}

export function useUser() {
  const context = useContext(UserContext);
  if (!context) {
    throw new Error("useUser must be used within UserProvider");
  }
  return context;
}

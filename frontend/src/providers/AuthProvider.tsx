"use client";
import { createContext, useContext, useEffect, useState, ReactNode } from "react";
import { onAuthStateChanged, User } from "firebase/auth";
import { auth } from "@/lib/firebase";

const Ctx = createContext<{ user: User | null; loading: boolean; emailVerified: boolean }>({ user: null, loading: true, emailVerified: false });
export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => onAuthStateChanged(auth, (u) => {
    setUser(u);
    setLoading(false);
  }), []);
  const emailVerified = user?.emailVerified ?? false;
  return <Ctx.Provider value={{ user, loading, emailVerified }}>{children}</Ctx.Provider>;
}
export const useAuth = () => useContext(Ctx);

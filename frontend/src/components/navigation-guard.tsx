"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, type ComponentProps, type ReactNode } from "react";
import Link from "next/link";

const UNSAVED_WARNING = "You have unsaved changes. Leave without saving?";
const NavigationGuard = createContext<{ register: () => () => void; confirmLeave: (href?: string) => boolean }>({
  register: () => () => {}, confirmLeave: () => true,
});

function staysOnPage(href: string): boolean {
  const destination = new URL(href, window.location.href);
  return destination.origin === window.location.origin && destination.pathname === window.location.pathname
    && destination.search === window.location.search;
}

export function NavigationGuardProvider({ children }: { children: ReactNode }) {
  const blockers = useRef(new Set<symbol>());
  const register = useCallback(() => {
    const token = Symbol("unsaved form");
    blockers.current.add(token);
    return () => { blockers.current.delete(token); };
  }, []);
  const confirmLeave = useCallback((href?: string) => {
    if ((href && staysOnPage(href)) || !blockers.current.size) return true;
    return window.confirm(UNSAVED_WARNING);
  }, []);

  useEffect(() => {
    // Unlike popstate, a cancelable Navigation API traversal can be stopped before Next unmounts the editor.
    // Do not patch history or insert synthetic history entries. beforeunload remains the document-leave fallback.
    const navigation = (window as Window & { navigation?: EventTarget }).navigation;
    if (!navigation) return;
    const traverse = (event: Event) => {
      const change = event as Event & { navigationType?: string; destination?: { url: string } };
      if (change.navigationType === "traverse" && change.cancelable && !confirmLeave(change.destination?.url)) change.preventDefault();
    };
    navigation.addEventListener("navigate", traverse);
    return () => navigation.removeEventListener("navigate", traverse);
  }, [confirmLeave]);

  const value = useMemo(() => ({ register, confirmLeave }), [register, confirmLeave]);
  return <NavigationGuard.Provider value={value}>{children}</NavigationGuard.Provider>;
}

export const useNavigationGuard = () => useContext(NavigationGuard);

export function useUnsavedChanges(dirty: boolean) {
  const { register } = useNavigationGuard();
  useEffect(() => {
    if (!dirty) return;
    const unregister = register();
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", warn);
    return () => { window.removeEventListener("beforeunload", warn); unregister(); };
  }, [dirty, register]);
}

export function GuardedLink({ href, onNavigate, ...props }: ComponentProps<typeof Link>) {
  const { confirmLeave } = useNavigationGuard();
  return <Link {...props} href={href} onNavigate={event => {
    if (!confirmLeave(typeof href === "string" ? href : undefined)) { event.preventDefault(); return; }
    onNavigate?.(event);
  }} />;
}

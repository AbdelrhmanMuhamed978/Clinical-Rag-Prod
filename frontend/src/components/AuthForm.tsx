"use client";

import { useState, useEffect } from "react";
import { signInWithEmailAndPassword, createUserWithEmailAndPassword, sendEmailVerification } from "firebase/auth";
import { auth } from "@/lib/firebase";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import dynamic from "next/dynamic";
import { motion, AnimatePresence } from "framer-motion";
import { Eye, EyeOff, Mail, Lock, User, CheckCircle, Loader2 } from "lucide-react";

const Background3D = dynamic(() => import("@/components/Background3D"), { ssr: false });

interface AuthFormProps {
  mode: "login" | "signup";
}

export default function AuthForm({ mode }: AuthFormProps) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [err, setErr] = useState("");
  const [loading, setLoading] = useState(false);
  const [flipped, setFlipped] = useState(mode === "signup");
  const [inputFocused, setInputFocused] = useState(false);
  const router = useRouter();

  // Sync URL with flip state
  useEffect(() => {
    const url = flipped ? "/signup" : "/login";
    if (window.location.pathname !== url) {
      window.history.replaceState(null, "", url);
    }
  }, [flipped]);

  async function handleLogin(e: React.FormEvent) {
    e.preventDefault();
    setErr("");
    setLoading(true);
    try {
      await signInWithEmailAndPassword(auth, email, password);
      router.push("/workspaces");
    } catch (e: any) {
      setErr(e.message);
    } finally {
      setLoading(false);
    }
  }

  async function handleSignup(e: React.FormEvent) {
    e.preventDefault();
    setErr("");
    if (password !== confirmPassword) {
      setErr("Passwords do not match");
      return;
    }
    if (password.length < 6) {
      setErr("Password must be at least 6 characters");
      return;
    }
    setLoading(true);
    try {
      const c = await createUserWithEmailAndPassword(auth, email, password);
      await sendEmailVerification(c.user);
      router.push("/verify-email");
    } catch (e: any) {
      setErr(e.message);
    } finally {
      setLoading(false);
    }
  }

  const isLogin = !flipped;

  return (
    <div className="flex min-h-screen items-center justify-center p-4 relative" style={{ perspective: 1200 }}>
      <Background3D />
      <div
        className="relative w-full max-w-md"
        style={{ transformStyle: "preserve-3d" }}
      >
        <AnimatePresence mode="wait">
          {isLogin ? (
            <motion.div
              key="login"
              initial={{ rotateY: 90, opacity: 0 }}
              animate={{ rotateY: 0, opacity: 1 }}
              exit={{ rotateY: -90, opacity: 0 }}
              transition={{ duration: 0.4, ease: [0.4, 0, 0.2, 1] }}
              style={{ backfaceVisibility: "hidden", transformStyle: "preserve-3d" }}
              aria-hidden={flipped}
              tabIndex={flipped ? -1 : 0}
            >
              <Card className="w-full glass-card shadow-xl">
                <CardHeader>
                  <CardTitle className="text-center text-2xl font-bold gradient-text">Sign in</CardTitle>
                  <p className="text-center text-sm text-muted-foreground mt-2">Welcome back to Clinical RAG</p>
                </CardHeader>
                <CardContent>
                  <form onSubmit={handleLogin} className="space-y-4">
                    <div className="space-y-2">
                      <Label htmlFor="email" className="text-sm font-medium">Email</Label>
                      <div className="relative">
                        <Mail className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                        <Input
                          id="email"
                          value={email}
                          onChange={e => setEmail(e.target.value)} onFocus={() => setInputFocused(true)} onBlur={() => setInputFocused(false)}
                          type="email"
                          required
                          className="pl-10"
                          placeholder="you@example.com"
                        />
                      </div>
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="password" className="text-sm font-medium">Password</Label>
                      <div className="relative">
                        <Lock className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                        <Input
                          id="password"
                          value={password}
                          onChange={e => setPassword(e.target.value)} onFocus={() => setInputFocused(true)} onBlur={() => setInputFocused(false)}
                          type={showPassword ? "text" : "password"}
                          required
                          className="pl-10 pr-10"
                          placeholder="••••••••"
                        />
                        <button
                          type="button"
                          onClick={() => setShowPassword(!showPassword)}
                          className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
                        >
                          {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                        </button>
                      </div>
                    </div>
                    {err && <p className="text-sm text-red-500">{err}</p>}
                    <Button type="submit" className="w-full gradient btn-press" disabled={loading}>
                      {loading ? <Loader2 className="w-4 h-4 mr-2 animate-spin" /> : null} Sign in
                    </Button>
                    <p className="text-sm text-center">
                      <button type="button" onClick={() => setFlipped(true)} className="text-primary hover:underline">
                        Create account
                      </button>
                    </p>
                  </form>
                </CardContent>
              </Card>
            </motion.div>
          ) : (
            <motion.div
              key="signup"
              initial={{ rotateY: 90, opacity: 0 }}
              animate={{ rotateY: 0, opacity: 1 }}
              exit={{ rotateY: -90, opacity: 0 }}
              transition={{ duration: 0.4, ease: [0.4, 0, 0.2, 1] }}
              style={{ backfaceVisibility: "hidden", transformStyle: "preserve-3d" }}
              aria-hidden={!flipped}
              tabIndex={!flipped ? -1 : 0}
            >
              <Card className="w-full glass-card shadow-xl">
                <CardHeader>
                  <CardTitle className="text-center text-2xl font-bold gradient-text">Create account</CardTitle>
                  <p className="text-center text-sm text-muted-foreground mt-2">Join Clinical RAG Platform</p>
                </CardHeader>
                <CardContent>
                  <form onSubmit={handleSignup} className="space-y-4">
                    <div className="space-y-2">
                      <Label htmlFor="email" className="text-sm font-medium">Email</Label>
                      <div className="relative">
                        <Mail className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                        <Input
                          id="email"
                          value={email}
                          onChange={e => setEmail(e.target.value)}
                          type="email"
                          required
                          className="pl-10"
                          placeholder="you@example.com"
                        />
                      </div>
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="password" className="text-sm font-medium">Password</Label>
                      <div className="relative">
                        <Lock className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                        <Input
                          id="password"
                          value={password}
                          onChange={e => setPassword(e.target.value)} onFocus={() => setInputFocused(true)} onBlur={() => setInputFocused(false)}
                          type={showPassword ? "text" : "password"}
                          required
                          minLength={6}
                          className="pl-10 pr-10"
                          placeholder="••••••••"
                        />
                        <button
                          type="button"
                          onClick={() => setShowPassword(!showPassword)}
                          className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
                        >
                          {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                        </button>
                      </div>
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="confirmPassword" className="text-sm font-medium">Confirm Password</Label>
                      <div className="relative">
                        <Lock className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                        <Input
                          id="confirmPassword"
                          value={confirmPassword}
                          onChange={e => setConfirmPassword(e.target.value)} onFocus={() => setInputFocused(true)} onBlur={() => setInputFocused(false)}
                          type={showPassword ? "text" : "password"}
                          required
                          className="pl-10"
                          placeholder="••••••••"
                        />
                      </div>
                      {confirmPassword && password === confirmPassword && (
                        <p className="text-xs text-emerald-600 flex items-center gap-1">
                          <CheckCircle className="w-3 h-3" /> Passwords match
                        </p>
                      )}
                    </div>
                    {err && <p className="text-sm text-red-500">{err}</p>}
                    <Button type="submit" className="w-full gradient btn-press" disabled={loading}>
                      {loading ? <Loader2 className="w-4 h-4 mr-2 animate-spin" /> : null} Create account
                    </Button>
                    <p className="text-sm text-center">
                      <button type="button" onClick={() => setFlipped(false)} className="text-primary hover:underline">
                        Already have an account? Sign in
                      </button>
                    </p>
                  </form>
                </CardContent>
              </Card>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </div>
  );
}
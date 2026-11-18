"use client";
import { useCallback, useEffect, useState } from "react";
import { auth } from "@/lib/firebase";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

export default function VerifyEmailPage() {
  const [checking, setChecking] = useState(false);
  const router = useRouter();
  const check = useCallback(async () => {
    setChecking(true);
    await auth.currentUser?.reload();
    if (auth.currentUser?.emailVerified) router.push("/workspaces");
    setChecking(false);
  }, [router]);
  useEffect(() => { const t = setInterval(check, 4000); return () => clearInterval(t); }, [check]);
  return (
    <div className="flex min-h-screen items-center justify-center p-4">
      <Card className="w-full max-w-md">
        <CardHeader><CardTitle>Verify your email</CardTitle></CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm">We sent a verification link to your email. Click it, then continue.</p>
          <Button onClick={check} disabled={checking} className="w-full">I&apos;ve verified</Button>
        </CardContent>
      </Card>
    </div>
  );
}

"use client";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useRef, useCallback } from "react";
import { get, post } from "@/lib/api";
import { Workspace } from "@/lib/types";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import TiltCard from "@/components/TiltCard";
import { motion } from "framer-motion";

export default function WorkspacesPage() {
  const qc = useQueryClient();
  const router = useRouter();
  const [name, setName] = useState("");
  const { data } = useQuery<Workspace[]>({ queryKey: ["workspaces"], queryFn: () => get("/api/workspaces") });
  const create = useMutation({
    mutationFn: (n: string) => post<Workspace>("/api/workspaces", { name: n }),
    onSuccess: (w) => { qc.invalidateQueries({ queryKey: ["workspaces"] }); router.push(`/w/${w.id}/documents`); },
  });
  const [tilt, setTilt] = useState<{ x: number; y: number; id: string | null }>({ x: 0, y: 0, id: null });

  const handleMouseMove = useCallback((e: React.MouseEvent<HTMLDivElement>, id: string) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = (e.clientX - rect.left) / rect.width;
    const y = (e.clientY - rect.top) / rect.height;
    setTilt({ x: (y - 0.5) * 8, y: (x - 0.5) * -8, id });
  }, []);

  const handleMouseLeave = useCallback(() => {
    setTilt({ x: 0, y: 0, id: null });
  }, []);

  return (
    <div className="max-w-3xl mx-auto space-y-6">
      <h1 className="text-2xl font-bold gradient-text">Workspaces</h1>
      <div className="flex gap-2">
        <Input value={name} onChange={e=>setName(e.target.value)} placeholder="New workspace name" />
        <Button onClick={() => name && create.mutate(name)} className="gradient">Create</Button>
      </div>
      <div className="grid gap-4 stagger-children">
        {data?.map((w, i) => (
          <motion.div
            key={w.id}
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.1, type: "spring", stiffness: 200, damping: 20 }}
          >
            <TiltCard className="cursor-pointer card-3d relative overflow-hidden" disabled={false}>
              <Card
                className="cursor-pointer card-3d relative overflow-hidden"
                onClick={() => router.push(`/w/${w.id}/documents`)}
              >
              {/* Specular highlight */}
              {tilt.id === w.id && (
                <div
                  className="absolute inset-0 pointer-events-none"
                  style={{
                    background: `radial-gradient(circle at ${50 + tilt.y * 5}% ${50 + tilt.x * 5}%, rgba(255,255,255,0.3) 0%, transparent 60%)`,
                  }}
                />
              )}
              <CardHeader><CardTitle>{w.name}</CardTitle></CardHeader>
              <CardContent className="text-sm text-muted-foreground">{w.id}</CardContent>
            </Card>
            </TiltCard>
          </motion.div>
        ))}
      </div>
    </div>
  );
}

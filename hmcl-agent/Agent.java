package com.pclinjector;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;

/**
 * HMCL Java Agent — 改写 RegionUtils.isIsChinaMainlandMachine/Network 返回 false
 * 支持两种入口:
 *   premain: 启动前 (-javaagent:)
 *   agentmain: 运行时 attach (VirtualMachine.attach.loadAgent)
 *
 * HMCL 未加载 RegionUtils 时 transform() 直接不命中 —— attach 场景用 retransformClasses 补丁已经加载的类
 */
public class Agent {

    private static Instrumentation currentInst;
    private static final RegionUtilsTransformer TRANSFORMER = new RegionUtilsTransformer();

    public static void premain(String agentArgs, Instrumentation inst) {
        System.out.println("[PCL-Injector] HMCL Java Agent (premain) loaded");
        install(inst);
    }

    public static void agentmain(String agentArgs, Instrumentation inst) {
        System.out.println("[PCL-Injector] HMCL Java Agent (agentmain attach) loaded");
        install(inst);
        // attach 场景: RegionUtils 可能已被 JVM 加载 —— retransform 补丁
        try {
            Class<?> target = Class.forName("org.jackhuang.hmcl.util.RegionUtils");
            inst.retransformClasses(target);
            System.out.println("[PCL-Injector] RegionUtils retransform OK");
        } catch (ClassNotFoundException e) {
            System.out.println("[PCL-Injector] RegionUtils not loaded yet (premain will catch it)");
        } catch (Exception e) {
            System.err.println("[PCL-Injector] retransform failed: " + e.getMessage());
        }
    }

    private static void install(Instrumentation inst) {
        currentInst = inst;
        inst.addTransformer(TRANSFORMER, true); // canRetransform=true
    }

    /** 运行时手动触发 retransform (给 AttachHelper 用) */
    public static boolean retransformRegionUtils() {
        if (currentInst == null) return false;
        try {
            Class<?> target = Class.forName("org.jackhuang.hmcl.util.RegionUtils");
            currentInst.retransformClasses(target);
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    static class RegionUtilsTransformer implements ClassFileTransformer {

        // iconst_0; ireturn; (返回 false)
        private static final byte[] RETURN_FALSE_BODY = { 0x03, (byte)0xAC };

        @Override
        public byte[] transform(
                ClassLoader loader,
                String className,
                Class<?> classBeingRedefined,
                ProtectionDomain protectionDomain,
                byte[] classfileBuffer) {

            if (!className.equals("org/jackhuang/hmcl/util/RegionUtils")) return null;

            System.out.println("[PCL-Injector] Patching RegionUtils...");
            try {
                byte[] patched = patchClass(classfileBuffer);
                if (patched != null) {
                    System.out.println("[PCL-Injector] RegionUtils patched OK");
                    return patched;
                }
            } catch (Exception e) {
                System.err.println("[PCL-Injector] Patch failed: " + e.getMessage());
            }
            return null;
        }

        private byte[] patchClass(byte[] raw) {
            if (raw.length < 10 || raw[0] != 0xCA || raw[1] != 0xFE || raw[2] != 0xBA || raw[3] != 0xBE)
                return null;
            int machineIdx = findUtf8Index(raw, "isIsChinaMainlandMachine");
            int networkIdx = findUtf8Index(raw, "isIsChinaMainlandNetwork");
            if (machineIdx == 0 && networkIdx == 0) return null;
            System.out.println("  isIsChinaMainlandMachine=#" + machineIdx + " network=#" + networkIdx);
            return rebuildClass(raw, machineIdx, networkIdx);
        }

        private static int findUtf8Index(byte[] raw, String target) {
            byte[] targetBytes = target.getBytes(java.nio.charset.StandardCharsets.UTF_8);
            int pos = 10, index = 1;
            while (pos < raw.length - 3) {
                byte tag = raw[pos];
                if (tag == 0x01) {
                    int len = ((raw[pos + 1] & 0xFF) << 8) | (raw[pos + 2] & 0xFF);
                    if (len == targetBytes.length && matchBytes(raw, pos + 3, targetBytes)) return index;
                    pos += 3 + len;
                } else {
                    pos = skipCpEntry(raw, pos);
                }
                index++;
            }
            return 0;
        }

        private static int skipCpEntry(byte[] raw, int pos) {
            byte tag = raw[pos]; pos++;
            switch (tag) {
                case 1:  { int len = ((raw[pos] & 0xFF) << 8) | (raw[pos + 1] & 0xFF); return pos + 2 + len; }
                case 7: case 8: case 16: case 19: case 20: return pos + 2;
                case 9: case 10: case 11: case 12: case 18: return pos + 4;
                case 3: case 4: case 14: return pos + 4;
                case 5: case 6: return pos + 8;
                default: return pos;
            }
        }

        private static boolean matchBytes(byte[] a, int start, byte[] b) {
            if (start + b.length > a.length) return false;
            for (int i = 0; i < b.length; i++) if (a[start + i] != b[i]) return false;
            return true;
        }

        private static int readU16(byte[] r, int p) { return (r[p] & 0xFF) << 8 | (r[p + 1] & 0xFF); }
        private static int readU32(byte[] r, int p) { return ((r[p] & 0xFF) << 24) | ((r[p + 1] & 0xFF) << 16) | ((r[p + 2] & 0xFF) << 8) | (r[p + 3] & 0xFF); }
        private static void writeU16(java.io.OutputStream s, int v) throws java.io.IOException { s.write((v >> 8) & 0xFF); s.write(v & 0xFF); }
        private static void writeU32(java.io.OutputStream s, int v) throws java.io.IOException { s.write((v >> 24) & 0xFF); s.write((v >> 16) & 0xFF); s.write((v >> 8) & 0xFF); s.write(v & 0xFF); }

        private static byte[] rebuildClass(byte[] raw, int machineIdx, int networkIdx) {
            try {
                java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
                out.write(raw, 0, 10);
                int cpCount = readU16(raw, 8);
                int cpEnd = findCpEnd(raw, cpCount);
                out.write(raw, 10, cpEnd - 10);
                int pos = cpEnd;

                int accessFlags = readU16(raw, pos); pos += 2;
                int thisClass = readU16(raw, pos); pos += 2;
                int superClass = readU16(raw, pos); pos += 2;
                int interfacesCount = readU16(raw, pos); pos += 2;
                writeU16(out, accessFlags); writeU16(out, thisClass); writeU16(out, superClass); writeU16(out, interfacesCount);
                out.write(raw, pos, interfacesCount * 2); pos += interfacesCount * 2;

                int fieldsCount = readU16(raw, pos); pos += 2; writeU16(out, fieldsCount);
                int fieldsEnd = skipMembers(raw, pos, fieldsCount);
                out.write(raw, pos, fieldsEnd - pos); pos = fieldsEnd;

                int methodsCount = readU16(raw, pos); pos += 2; writeU16(out, methodsCount);
                for (int i = 0; i < methodsCount; i++) {
                    int methodStart = pos;
                    int mAccess = readU16(raw, pos); pos += 2;
                    int mNameIdx = readU16(raw, pos); pos += 2;
                    int mDescIdx = readU16(raw, pos); pos += 2;
                    int mAttrsCount = readU16(raw, pos); pos += 2;
                    boolean isTarget = (mNameIdx == machineIdx || mNameIdx == networkIdx);

                    if (!isTarget) {
                        int attrsSize = 0;
                        for (int a = 0; a < mAttrsCount; a++) { attrsSize += 6; attrsSize += readU32(raw, pos + 2); pos += 6 + attrsSize - (attrsSize - readU32(raw, pos + 2)); }
                        // 简化: 原样拷贝
                        int copyStart = methodStart + 8;
                        int copyLen = 0;
                        for (int a = 0; a < mAttrsCount; a++) { copyLen += 6 + readU32(raw, copyStart + 2); copyStart += 6 + readU32(raw, copyStart + 2); }
                        out.write(raw, methodStart, 8 + copyLen);
                        pos = methodStart + 8 + copyLen;
                    } else {
                        writeU16(out, mAccess); writeU16(out, mNameIdx); writeU16(out, mDescIdx); writeU16(out, mAttrsCount);
                        for (int a = 0; a < mAttrsCount; a++) {
                            int attrNameIdx = readU16(raw, pos); pos += 2;
                            int attrLen = readU32(raw, pos); pos += 4;
                            String attrName = getUtf8FromCp(raw, attrNameIdx, cpCount);
                            if ("Code".equals(attrName)) {
                                pos += attrLen;
                                writeU16(out, attrNameIdx);
                                writeU32(out, 12);
                                writeU16(out, 1); writeU16(out, 0); writeU32(out, 2);
                                out.write(RETURN_FALSE_BODY);
                                writeU16(out, 0);
                            } else {
                                writeU16(out, attrNameIdx); writeU32(out, attrLen);
                                out.write(raw, pos, attrLen); pos += attrLen;
                            }
                        }
                    }
                }

                int classAttrsCount = readU16(raw, pos); pos += 2; writeU16(out, classAttrsCount);
                for (int a = 0; a < classAttrsCount; a++) {
                    int attrNameIdx = readU16(raw, pos); pos += 2;
                    int attrLen = readU32(raw, pos); pos += 4;
                    writeU16(out, attrNameIdx); writeU32(out, attrLen);
                    out.write(raw, pos, attrLen); pos += attrLen;
                }
                return out.toByteArray();
            } catch (Exception e) { throw new RuntimeException("Rebuild class failed", e); }
        }

        private static int findCpEnd(byte[] raw, int cpCount) { int pos = 10; for (int i = 1; i < cpCount; i++) { int next = skipCpEntry(raw, pos); if (next == pos) break; pos = next; if (pos >= raw.length) break; } return pos; }
        private static int skipMembers(byte[] raw, int start, int count) { int pos = start; for (int i = 0; i < count; i++) { pos += 8; int attrsCount = readU16(raw, pos - 2); for (int a = 0; a < attrsCount; a++) { pos += 6 + readU32(raw, pos + 2); } } return pos; }
        private static String getUtf8FromCp(byte[] raw, int cpIndex, int cpCount) {
            if (cpIndex == 0 || cpIndex >= cpCount) return "";
            int pos = 10; for (int i = 1; i < cpIndex; i++) { pos = skipCpEntry(raw, pos); if (pos >= raw.length) break; }
            if (pos + 3 > raw.length || raw[pos] != 0x01) return "";
            int len = ((raw[pos + 1] & 0xFF) << 8) | (raw[pos + 2] & 0xFF);
            if (pos + 3 + len > raw.length) return "";
            return new String(raw, pos + 3, len, java.nio.charset.StandardCharsets.UTF_8);
        }
    }
}

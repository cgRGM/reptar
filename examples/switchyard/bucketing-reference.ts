// Independent test oracle: BigInt arithmetic rather than C# uint overflow.
export const bucket = (fields: string[]): number => {
  let hash = 2_166_136_261n;
  for (const field of fields) {
    const bytes = new TextEncoder().encode(field);
    const prefix = new Uint8Array(4);
    new DataView(prefix.buffer).setUint32(0, bytes.length, true);
    for (const byte of [...prefix, ...bytes]) {
      // eslint-disable-next-line no-bitwise -- XOR is part of the specified FNV-1a algorithm.
      hash = BigInt.asUintN(32, (hash ^ BigInt(byte)) * 16_777_619n);
    }
  }
  return Number(hash % 100n);
};

import { useCallback, useEffect, useRef, useState } from "react";
import type { ChangeEvent, Dispatch, FormEvent, SetStateAction } from "react";

export interface ToolCallInfo {
  id: string;
  name: string;
  args?: Record<string, unknown> | string;
  state: "running" | "ok" | "bad";
  result?: string;
  ms?: number;
}

export interface AgentMessage {
  id: string;
  role: "user" | "assistant" | "tool" | "system";
  content: string;
  name?: string;
  toolCalls?: ToolCallInfo[];
  steps?: string[];
  isStreaming?: boolean;
  createdAt?: string;
}

export interface AgentStreamEvent {
  type:
    | "turn_start"
    | "text"
    | "tool_start"
    | "tool_end"
    | "usage"
    | "error"
    | "done";
  step?: number;
  text?: string;
  id?: string;
  name?: string;
  args?: string | Record<string, unknown>;
  success?: boolean;
  result?: string;
  elapsedMs?: number;
  inputTokens?: number;
  outputTokens?: number;
  message?: string;
  sessionId?: string;
  stopReason?: string;
}

export interface UseAgentChatOptions {
  endpoint?: string;
  initialMessages?: AgentMessage[];
  sessionId?: string | null;
  provider?: string;
  model?: string;
  headers?: Record<string, string>;
  onEvent?: (event: AgentStreamEvent) => void;
  onFinish?: (message: AgentMessage) => void;
  onError?: (error: Error) => void;
}

export interface UseAgentChatReturn {
  messages: AgentMessage[];
  input: string;
  setInput: Dispatch<SetStateAction<string>>;
  handleInputChange: (
    e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>
  ) => void;
  send: (customText?: string) => Promise<void>;
  handleSubmit: (e?: FormEvent) => void;
  isStreaming: boolean;
  isLoading: boolean;
  stop: () => void;
  clear: () => void;
  sessionId: string | null;
  setSessionId: Dispatch<SetStateAction<string | null>>;
  error: Error | null;
  activeSteps: string[];
}

const generateId = (): string =>
  `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;

/**
 * useAgentChat — Official React hook for Reptar's streaming agent harness.
 * Streams SSE frames from `POST /api/chat`, manages conversation turns,
 * step-by-step reasoning traces, in-process tool executions, and sessions.
 */
export const useAgentChat = (
  options: UseAgentChatOptions = {}
): UseAgentChatReturn => {
  const {
    endpoint = "/api/chat",
    initialMessages = [],
    sessionId: initialSessionId = null,
    provider,
    model,
    headers,
    onEvent,
    onFinish,
    onError,
  } = options;

  const [messages, setMessages] = useState<AgentMessage[]>(initialMessages);
  const [input, setInput] = useState<string>("");
  const [isStreaming, setIsStreaming] = useState<boolean>(false);
  const [sessionId, setSessionId] = useState<string | null>(initialSessionId);
  const [error, setError] = useState<Error | null>(null);
  const [activeSteps, setActiveSteps] = useState<string[]>([]);

  const abortControllerRef = useRef<AbortController | null>(null);

  const handleInputChange = useCallback(
    (e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      setInput(e.target.value);
    },
    []
  );

  const stop = useCallback(() => {
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
      abortControllerRef.current = null;
    }
    setIsStreaming(false);
  }, []);

  const clear = useCallback(() => {
    stop();
    setMessages([]);
    setActiveSteps([]);
    setError(null);
  }, [stop]);

  const send = useCallback(
    async (customText?: string) => {
      const text = (customText ?? input).trim();
      if (!text || isStreaming) {
        return;
      }

      setError(null);
      setInput("");
      setIsStreaming(true);

      const userMessage: AgentMessage = {
        content: text,
        createdAt: new Date().toISOString(),
        id: generateId(),
        role: "user",
      };

      setMessages((prev) => [...prev, userMessage]);

      const assistantId = generateId();
      const initialAssistantMessage: AgentMessage = {
        content: "",
        createdAt: new Date().toISOString(),
        id: assistantId,
        isStreaming: true,
        role: "assistant",
        steps: [],
        toolCalls: [],
      };

      setMessages((prev) => [...prev, initialAssistantMessage]);

      const controller = new AbortController();
      abortControllerRef.current = controller;

      let accumulatedContent = "";
      const currentTools: ToolCallInfo[] = [];
      const currentSteps: string[] = [];

      try {
        const response = await fetch(endpoint, {
          body: JSON.stringify({
            message: text,
            model,
            provider,
            sessionId,
          }),
          headers: {
            "content-type": "application/json",
            ...headers,
          },
          method: "POST",
          signal: controller.signal,
        });

        if (!response.ok || !response.body) {
          throw new Error(`Agent request failed with HTTP ${response.status}`);
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        const updateAssistantState = () => {
          setMessages((prev) => {
            const copy = [...prev];
            const lastIdx = copy.length - 1;
            if (lastIdx >= 0 && copy[lastIdx]?.id === assistantId) {
              copy[lastIdx] = {
                ...copy[lastIdx],
                content: accumulatedContent,
                isStreaming: true,
                steps: [...currentSteps],
                toolCalls: [...currentTools],
              };
            }
            return copy;
          });
        };

        const processFrame = (dataStr: string) => {
          if (!dataStr || dataStr === "[DONE]") {
            return;
          }

          let event: AgentStreamEvent;
          try {
            event = JSON.parse(dataStr) as AgentStreamEvent;
          } catch {
            return;
          }

          onEvent?.(event);

          switch (event.type) {
            case "turn_start": {
              if (event.step !== undefined) {
                const stepLabel = `Step ${event.step}`;
                currentSteps.push(stepLabel);
                setActiveSteps([...currentSteps]);
                updateAssistantState();
              }
              break;
            }

            case "text": {
              if (event.text) {
                accumulatedContent += event.text;
                updateAssistantState();
              }
              break;
            }

            case "tool_start": {
              if (event.id && event.name) {
                currentTools.push({
                  args: event.args,
                  id: event.id,
                  name: event.name,
                  state: "running",
                });
                updateAssistantState();
              }
              break;
            }

            case "tool_end": {
              const targetIdx = currentTools.findIndex(
                (t) => t.id === event.id
              );
              const idx =
                targetIdx !== -1 ? targetIdx : currentTools.length - 1;
              if (idx >= 0 && currentTools[idx]) {
                currentTools[idx] = {
                  ...currentTools[idx],
                  ms: event.elapsedMs,
                  result: event.result,
                  state: event.success ? "ok" : "bad",
                };
                updateAssistantState();
              }
              break;
            }

            case "done": {
              if (event.sessionId) {
                setSessionId(event.sessionId);
              }
              break;
            }

            case "error": {
              const errText = `\n\n> ⚠️ **Error:** ${event.message ?? "Unknown error"}`;
              accumulatedContent += errText;
              updateAssistantState();
              break;
            }
          }
        };

        while (true) {
          const { done, value } = await reader.read();
          if (done) {
            break;
          }

          buffer += decoder.decode(value, { stream: true });
          let separatorIdx: number;

          while ((separatorIdx = buffer.indexOf("\n\n")) >= 0) {
            const chunk = buffer.slice(0, separatorIdx);
            buffer = buffer.slice(separatorIdx + 2);

            if (chunk.startsWith("data:")) {
              processFrame(chunk.slice(5).trim());
            }
          }
        }

        const finalAssistantMessage: AgentMessage = {
          content: accumulatedContent,
          createdAt: new Date().toISOString(),
          id: assistantId,
          isStreaming: false,
          role: "assistant",
          steps: currentSteps,
          toolCalls: currentTools,
        };

        setMessages((prev) => {
          const copy = [...prev];
          const lastIdx = copy.length - 1;
          if (lastIdx >= 0 && copy[lastIdx]?.id === assistantId) {
            copy[lastIdx] = finalAssistantMessage;
          }
          return copy;
        });

        onFinish?.(finalAssistantMessage);
      } catch (err: unknown) {
        if ((err as Error)?.name === "AbortError") {
          return;
        }

        const normalizedError =
          err instanceof Error ? err : new Error(String(err));
        setError(normalizedError);
        onError?.(normalizedError);

        setMessages((prev) => {
          const copy = [...prev];
          const lastIdx = copy.length - 1;
          if (lastIdx >= 0 && copy[lastIdx]?.id === assistantId) {
            copy[lastIdx] = {
              ...copy[lastIdx],
              content:
                accumulatedContent +
                `\n\n**Error:** ${normalizedError.message}`,
              isStreaming: false,
            };
          }
          return copy;
        });
      } finally {
        setIsStreaming(false);
        abortControllerRef.current = null;
      }
    },
    [
      endpoint,
      headers,
      input,
      isStreaming,
      model,
      onError,
      onEvent,
      onFinish,
      provider,
      sessionId,
    ]
  );

  const handleSubmit = useCallback(
    (e?: FormEvent) => {
      e?.preventDefault();
      void send();
    },
    [send]
  );

  useEffect(() => {
    return () => {
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, []);

  return {
    activeSteps,
    clear,
    error,
    handleInputChange,
    handleSubmit,
    input,
    isLoading: isStreaming,
    isStreaming,
    messages,
    send,
    sessionId,
    setInput,
    setSessionId,
    stop,
  };
};

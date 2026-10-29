import { useCallback, useEffect, useRef, useState } from "react";
import { Keyboard } from "react-native";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { createDomesticProductBatch, fetchDomesticSuppliers, fetchProductPrefixes, fetchDomesticSetTemplates, fetchDomesticSetTemplate, saveDomesticSetTemplate } from "./api";
import { applyTemplate, batchAddDrafts, buildBatchItems, buildTemplatePayload, createRequestScope, createSubmissionGate, DraftValidationError, isExplicitCreateBusinessRejection, newProduct, summarizeBatch } from "./create-batch-draft";
import type { ProductDraft } from "./create-batch-draft";
import type { CreateDomesticProductBatchResult, DomesticSetTemplateSummary, DomesticSupplierOption, ProductPrefixOption, ProductCreationType } from "./types";

export interface CreateBatchCallbacks {
  onDismiss: () => void;
  onReturnToList: () => void;
  onCreated: (result: CreateDomesticProductBatchResult, supplier: DomesticSupplierOption, prefix: ProductPrefixOption | null) => void;
}

export type CreatePanel = "supplier" | "prefix" | "templates" | "batch" | "saveTemplate" | null;

export function useCreateBatch({ onDismiss, onReturnToList, onCreated }: CreateBatchCallbacks) {
  const { t, language } = useAppTranslation(["domesticPurchase", "common"]);
  const [initial] = useState(newProduct);
  const [products, setProducts] = useState<ProductDraft[]>([initial]);
  const [step, setStep] = useState(0);
  const [panel, setPanel] = useState<CreatePanel>(null);
  const [query, setQuery] = useState("");
  const [notice, setNotice] = useState("");
  const [supplier, setSupplier] = useState<DomesticSupplierOption | null>(null);
  const [prefix, setPrefix] = useState<ProductPrefixOption | null>(null);
  const [suppliers, setSuppliers] = useState<DomesticSupplierOption[]>([]);
  const [prefixes, setPrefixes] = useState<ProductPrefixOption[]>([]);
  const [templates, setTemplates] = useState<DomesticSetTemplateSummary[]>([]);
  const [supplierLoading, setSupplierLoading] = useState(false);
  const [prefixLoading, setPrefixLoading] = useState(false);
  const [templateLoading, setTemplateLoading] = useState(false);
  const [supplierError, setSupplierError] = useState("");
  const [prefixError, setPrefixError] = useState("");
  const [templateError, setTemplateError] = useState("");
  const [busy, setBusy] = useState(false);
  const [creationUncertain, setCreationUncertain] = useState(false);
  const [templateSource, setTemplateSource] = useState<string | null>(null);
  const [templateName, setTemplateName] = useState("");
  const [gate] = useState(createSubmissionGate);
  const [supplierScope] = useState(createRequestScope);
  const [prefixScope] = useState(createRequestScope);
  const [templateScope] = useState(createRequestScope);
  const [templateDetailScope] = useState(createRequestScope);
  const alive = useRef(true);
  const supplierCode = useRef("");
  const submissionOutcome = useRef<"ready" | "succeeded" | "uncertain">("ready");

  const errorMessage = useCallback((error: unknown, fallbackKey: string) => {
    if (error instanceof DraftValidationError) {
      const suffix = !error.row && (error.code === "invalidPrice" || error.code === "missingSubItems") ? "General" : "";
      return t(`wizard.errors.${error.code}${suffix}`, { row: error.row, subRow: error.subRow });
    }
    return resolveLocalizedErrorMessage(error, { language, t, fallbackKey });
  }, [language, t]);

  const loadSuppliers = useCallback(async () => {
    const current = supplierScope.begin();
    setSupplierLoading(true);
    setSupplierError("");
    try {
      const items = await fetchDomesticSuppliers();
      if (current()) setSuppliers(items);
    } catch (error) {
      if (current()) setSupplierError(errorMessage(error, "messages.loadSuppliersFailed"));
    } finally { if (current()) setSupplierLoading(false); }
  }, [errorMessage, supplierScope]);

  useEffect(() => {
    alive.current = true;
    void loadSuppliers();
    return () => {
      alive.current = false;
      supplierScope.invalidate(); prefixScope.invalidate(); templateScope.invalidate(); templateDetailScope.invalidate();
    };
  }, [loadSuppliers, supplierScope, prefixScope, templateScope, templateDetailScope]);

  const loadPrefixes = async (code = supplierCode.current) => {
    if (!code) return;
    const current = prefixScope.begin();
    setPrefixLoading(true); setPrefixError("");
    try {
      const items = await fetchProductPrefixes(code);
      if (current()) setPrefixes(items);
    } catch (error) {
      if (current()) setPrefixError(errorMessage(error, "messages.loadPrefixesFailed"));
    } finally { if (current()) setPrefixLoading(false); }
  };

  const loadTemplates = async (code = supplierCode.current) => {
    if (!code) return;
    const current = templateScope.begin();
    setTemplateLoading(true); setTemplateError("");
    try {
      const items = await fetchDomesticSetTemplates(code);
      if (current()) setTemplates(items);
    } catch (error) {
      if (current()) setTemplateError(errorMessage(error, "wizard.loadTemplatesFailed"));
    } finally { if (current()) setTemplateLoading(false); }
  };

  const openPanel = useCallback((next: CreatePanel) => {
    Keyboard.dismiss(); setQuery(""); setNotice(""); setPanel(next);
  }, []);

  const openSaveTemplate = useCallback((product: ProductDraft) => {
    setTemplateSource(product.key); setTemplateName(product.productName); openPanel("saveTemplate");
  }, [openPanel]);

  const selectSupplier = (item: DomesticSupplierOption) => {
    if (gate.busy) return;
    if (supplierCode.current !== item.supplierCode) {
      // 先失效旧请求和选项，再发起新请求；商品草稿按 Web 行为保留。
      prefixScope.invalidate(); templateScope.invalidate(); templateDetailScope.invalidate();
      supplierCode.current = item.supplierCode;
      setSupplier(item); setPrefix(null); setPrefixes([]); setTemplates([]);
      setTemplateSource(null); setTemplateName("");
      void loadPrefixes(item.supplierCode); void loadTemplates(item.supplierCode);
    }
    openPanel(null);
  };

  const next = () => {
    Keyboard.dismiss(); setNotice("");
    if (!supplier) { setNotice(t("messages.selectSupplier")); return; }
    try {
      if (step === 1) buildBatchItems(products);
      setStep((value) => Math.min(2, value + 1));
    } catch (error) { setNotice(errorMessage(error, "messages.createFailed")); }
  };

  const submit = () => {
    // 终态写在 ref 中，先于 React 重绘拦截快速连点和程序化重复调用。
    if (submissionOutcome.current !== "ready") return;
    return gate.run(async () => {
      if (submissionOutcome.current !== "ready") return;
      if (!supplier) { setNotice(t("messages.selectSupplier")); return; }
      setNotice("");
      let items;
      try {
        items = buildBatchItems(products);
      } catch (error) {
        if (alive.current) setNotice(errorMessage(error, "messages.createFailed"));
        return;
      }

      let result: CreateDomesticProductBatchResult;
      Keyboard.dismiss(); setBusy(true);
      try {
        result = await createDomesticProductBatch({
          supplierCode: supplier.supplierCode,
          prefixCode: prefix?.prefixCode || undefined,
          prefixName: prefix?.prefixName || undefined,
          items,
        });
      } catch (error) {
        if (isExplicitCreateBusinessRejection(error)) {
          if (alive.current) { setNotice(errorMessage(error, "messages.createFailed")); setBusy(false); }
        } else {
          submissionOutcome.current = "uncertain";
          if (alive.current) {
            setCreationUncertain(true);
            setNotice(t("wizard.creationResultUncertain"));
            setBusy(false);
          }
        }
        return;
      }
      submissionOutcome.current = "succeeded";
      if (alive.current) setBusy(false);
      // 写入成功之后交给页面读取结果；读取失败绝不能回到可重复提交的创建状态。
      if (alive.current) onCreated(result, supplier, prefix);
    });
  };

  const apply = (templateId: string) => gate.run(async () => {
    const code = supplierCode.current;
    const current = templateDetailScope.begin();
    setBusy(true); setNotice("");
    try {
      const template = await fetchDomesticSetTemplate(templateId, code);
      if (!current() || supplierCode.current !== code) return;
      setProducts((items) => applyTemplate(items, template, initial.key));
      openPanel(null);
    } catch (error) {
      if (current()) setNotice(errorMessage(error, "wizard.loadTemplatesFailed"));
    } finally { if (alive.current) setBusy(false); }
  });

  const saveTemplate = () => gate.run(async () => {
    const product = products.find((item) => item.key === templateSource);
    if (!product || !supplier) return;
    setNotice("");
    try {
      const payload = buildTemplatePayload(supplier.supplierCode, templateName, product);
      setBusy(true);
      await saveDomesticSetTemplate(payload);
      if (alive.current) {
        openPanel(null); setNotice(t("wizard.templateSaved"));
        void loadTemplates();
      }
    } catch (error) {
      if (alive.current) setNotice(errorMessage(error, "wizard.saveTemplateFailed"));
    } finally { if (alive.current) setBusy(false); }
  });

  const addBatch = (type: ProductCreationType, count: string, price: string, mode: "append" | "overwrite") => {
    try {
      setProducts(batchAddDrafts(products, type, count, price, mode));
      openPanel(null);
    } catch (error) { setNotice(errorMessage(error, "messages.createFailed")); }
  };

  return {
    t, language, products, setProducts, step, panel, query, setQuery, notice, supplier, prefix, suppliers, prefixes, templates,
    supplierLoading, prefixLoading, templateLoading, supplierError, prefixError, templateError, busy, creationUncertain,
    templateName, setTemplateName, totals: summarizeBatch(products),
    loadSuppliers, loadPrefixes, loadTemplates, openPanel, selectSupplier, next, submit, apply, saveTemplate, addBatch,
    selectPrefix(item: ProductPrefixOption | null) { setPrefix(item); openPanel(null); },
    openSaveTemplate,
    back() { if (gate.busy) return; setNotice(""); Keyboard.dismiss(); if (panel) openPanel(null); else setStep((value) => Math.max(0, value - 1)); },
    returnToList() { if (!gate.busy) { Keyboard.dismiss(); onReturnToList(); } },
    dismiss() { if (!gate.busy) { Keyboard.dismiss(); if (submissionOutcome.current === "uncertain") onReturnToList(); else onDismiss(); } },
  };
}

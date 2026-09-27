(async () => {
    const checkedMat = file.ReadImageMatSync("assets/bag_preciousitem_checked.png");
    const uncheckedMat = file.ReadImageMatSync("assets/bag_preciousitem_unchecked.png");
    try {
        const checked = RecognitionObject.TemplateMatch(checkedMat);
        const unchecked = RecognitionObject.TemplateMatch(uncheckedMat);
        checked.Threshold = 0.8;
        unchecked.Threshold = 0.87;
        await genshin.returnMainUi();
        keyPress("B");
        await sleep(1500);
        let selected = false;
        for (let attempt = 0; attempt < 5; attempt++) {
            const frame = captureGameRegion();
            try {
                if (frame.Width !== 1920 || frame.Height !== 1080) throw new Error("PROBE_UNEXPECTED_FRAME_SIZE");
                const top = frame.DeriveCrop(0, 0, 1920, 110);
                try {
                    const current = top.find(checked);
                    try { selected = current.isExist(); } finally { current.dispose(); }
                    if (selected) {
                        if (!file.WriteImageSync("evidence/precious-page.png", frame.SrcMat)) throw new Error("PROBE_IMAGE_WRITE_FAILED");
                        if (typeof settings !== "undefined" && settings.capturePages === true) {
                            moveMouseTo(700, 500);
                            await sleep(60);
                            verticalScroll(50);
                            await sleep(600);
                            for (let page = 0; page < 13; page++) {
                                const currentPage = captureGameRegion();
                                try {
                                    if (currentPage.Width !== 1920 || currentPage.Height !== 1080) throw new Error("PROBE_UNEXPECTED_FRAME_SIZE");
                                    const header = currentPage.DeriveCrop(0, 0, 1920, 110);
                                    try {
                                        const marker = header.find(checked);
                                        try { if (!marker.isExist()) throw new Error("PROBE_PAGE_CHANGED"); }
                                        finally { marker.dispose(); }
                                    } finally { header.dispose(); }
                                    if (!file.WriteImageSync("evidence/precious-scroll-" + page + ".png", currentPage.SrcMat)) throw new Error("PROBE_IMAGE_WRITE_FAILED");
                                } finally { currentPage.dispose(); }
                                if (page < 12) {
                                    verticalScroll(-3);
                                    await sleep(500);
                                }
                            }
                        }
                        file.WriteTextSync("evidence/result.json", JSON.stringify({status: "captured", page: "PreciousItems", itemUse: false}));
                        log.info("RDP_INVENTORY_PROBE_CAPTURED page=PreciousItems itemUse=false");
                        return;
                    }
                    const target = top.find(unchecked);
                    try { if (target.isExist()) target.click(); } finally { target.dispose(); }
                } finally { top.dispose(); }
            } finally { frame.dispose(); }
            await sleep(1000);
        }
        throw new Error("PROBE_PRECIOUS_PAGE_NOT_CONFIRMED");
    } finally {
        checkedMat.dispose();
        uncheckedMat.dispose();
    }
})();

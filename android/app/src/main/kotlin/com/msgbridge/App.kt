package com.msgbridge

import android.app.Application
import com.msgbridge.core.Prefs

class App : Application() {
    override fun onCreate() {
        super.onCreate()
        Prefs.init(this)
    }
}
